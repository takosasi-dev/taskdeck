using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Backup;

/// <summary>
/// DB のバックアップと復元（F-101〜F-103）。コピーは SQLite のオンラインバックアップ API で行う
/// （WAL に残っているコミット済みの変更も含めて、一貫した状態を写すため。ファイルの単純コピーはしない）。
/// 起動時バックアップは backups\taskdeck_yyyyMMdd_HHmmss.db に5世代まで残す。
/// </summary>
public sealed partial class BackupService(TaskDeckDataOptions options, IClock clock, ILogger<BackupService> logger)
{
    public const int Generations = 5;

    [GeneratedRegex(@"^taskdeck_\d{8}_\d{6}(_\d+)?\.db$", RegexOptions.IgnoreCase)]
    private static partial Regex StartupBackupName();

    /// <summary>起動時バックアップ。DB が無ければ何もしないで null。作ったファイルのパスを返す。</summary>
    public string? CreateStartupBackup()
    {
        if (!File.Exists(options.DatabasePath))
        {
            return null;
        }
        Directory.CreateDirectory(options.BackupDirectory);
        var path = UniquePath(options.BackupDirectory, "taskdeck_" + LocalStamp());
        Copy(options.DatabasePath, path);
        Prune();
        logger.LogInformation("起動時バックアップを作成しました: {Path}", path);
        return path;
    }

    /// <summary>手動バックアップ（F-102）。任意の場所へ書き出す。</summary>
    public void CreateBackup(string destinationPath)
    {
        var full = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Copy(options.DatabasePath, full);
        logger.LogInformation("手動バックアップを作成しました: {Path}", full);
    }

    /// <summary>起動時バックアップの一覧（新しい順）。</summary>
    public IReadOnlyList<FileInfo> ListBackups()
    {
        if (!Directory.Exists(options.BackupDirectory))
        {
            return [];
        }
        return new DirectoryInfo(options.BackupDirectory)
            .GetFiles("*.db")
            .Where(f => StartupBackupName().IsMatch(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// バックアップから復元する（F-103）。呼んだ後はアプリを再起動すること。
    /// 復元前の DB は backups\prerestore_*.db に退避する（二重に守る）。壊れたファイルなら何もせず InvalidDataException。
    /// </summary>
    public void RestoreFrom(string backupPath)
    {
        var source = Path.GetFullPath(backupPath);
        Validate(source);
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(options.BackupDirectory);
        if (File.Exists(options.DatabasePath))
        {
            var safety = UniquePath(options.BackupDirectory, "prerestore_" + LocalStamp());
            Copy(options.DatabasePath, safety);
            logger.LogInformation("復元前の DB を退避しました: {Path}", safety);
        }
        Copy(source, options.DatabasePath);
        logger.LogInformation("バックアップから復元しました: {Path}", source);
    }

    /// <summary>古い起動時バックアップを消して Generations 件に保つ。</summary>
    public void Prune()
    {
        foreach (var old in ListBackups().Skip(Generations))
        {
            try
            {
                old.Delete();
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "古いバックアップを消せませんでした: {Path}", old.FullName);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "古いバックアップを消せませんでした: {Path}", old.FullName);
            }
        }
    }

    /// <summary>SQLite として開けて、整合性検査が通り、TaskItem 表があるか。</summary>
    public static void Validate(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("バックアップファイルが見つかりません。", path);
        }
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            conn.Open();
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            var result = check.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("バックアップファイルが壊れています（整合性検査に失敗）。");
            }
            using var table = conn.CreateCommand();
            table.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='TaskItem';";
            if (Convert.ToInt64(table.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                throw new InvalidDataException("TaskDeck のバックアップではありません。");
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("SQLite のファイルとして開けません。", ex);
        }
    }

    /// <summary>オンラインバックアップ API で source の内容を destination に写す（destination は丸ごと置き換わる）。</summary>
    internal static void Copy(string sourcePath, string destinationPath)
    {
        using var source = new SqliteConnection(TaskDeckDataOptions.BuildConnectionString(sourcePath, pooling: false));
        using var destination = new SqliteConnection(TaskDeckDataOptions.BuildConnectionString(destinationPath, pooling: false));
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private string LocalStamp() => clock.LocalNow().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    private static string UniquePath(string directory, string baseName)
    {
        var path = Path.Combine(directory, baseName + ".db");
        for (var i = 1; File.Exists(path); i++)
        {
            path = Path.Combine(directory, $"{baseName}_{i}.db");
        }
        return path;
    }
}

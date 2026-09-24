using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Time;
using TaskDeck.Data.Backup;

namespace TaskDeck.App.Settings.Pages;

/// <summary>起動時バックアップの1件（F-101）。</summary>
public sealed record BackupEntry(string FullPath, string When, string Size)
{
    /// <summary>一覧の項目としての読み上げ名（UI Automation は項目の ToString を使う）。</summary>
    public override string ToString() => $"{When} のバックアップ（{Size}）";
}

/// <summary>
/// データ（F-101〜F-103・F-127）。担当: 波1-D。
/// 取り込み・書き出し（F-104〜）は波2-G がこのページに足す。
/// </summary>
public sealed partial class DataPageViewModel(
    AppPaths paths,
    BackupService backups,
    IClock clock,
    ILogger<DataPageViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private string _status = "";

    public string DatabasePath => paths.DatabasePath;

    public string DataDirectory => paths.DataDirectory;

    public string BackupDirectory => paths.BackupDirectory;

    /// <summary>起動時バックアップ（新しい順、最大5世代）。</summary>
    public ObservableCollection<BackupEntry> Backups { get; } = [];

    public bool HasBackups => Backups.Count > 0;

    /// <summary>
    /// 手動バックアップの既定のファイル名（F-102）。起動時バックアップ（taskdeck_yyyyMMdd_HHmmss.db）と
    /// 違う名前にする。同じ形だと backups に置かれたとき起動時の世代に数えられ、古い順に消されてしまうため。
    /// </summary>
    public string SuggestedBackupName =>
        "taskdeck_manual_" + clock.LocalNow().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".db";

    /// <summary>手動バックアップの書き出し先の初期フォルダ（ドキュメント）。</summary>
    public static string SuggestedBackupFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public void Refresh()
    {
        Backups.Clear();
        foreach (var file in backups.ListBackups())
        {
            Backups.Add(new BackupEntry(file.FullName, WhenOf(file), FormatSize(file.Length)));
        }
        OnPropertyChanged(nameof(HasBackups));
    }

    /// <summary>
    /// 作った日時。起動時バックアップの名前（taskdeck_yyyyMMdd_HHmmss）はその時のローカル時刻なので、それを出す
    /// （ファイルの更新日時はコピーや同期で変わることがある）。名前から読めなければ更新日時。
    /// </summary>
    private string WhenOf(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var stamp = name.Length >= 24 ? name.Substring(9, 15) : "";
        var local = DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : clock.ToLocal(file.LastWriteTimeUtc);
        return local.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>エクスプローラでフォルダを開く（F-127）。</summary>
    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            Status = "フォルダが見つかりませんでした";
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>手動バックアップ（F-102）。書き出せたら true。</summary>
    public bool CreateBackup(string destinationPath)
    {
        try
        {
            backups.CreateBackup(destinationPath);
            Status = $"バックアップを作りました: {destinationPath}";
            Refresh();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogError(ex, "手動バックアップに失敗しました");
            Status = $"バックアップを作れませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// バックアップから復元（F-103）。成功したら呼び出し側がアプリを終了させる。
    /// 壊れたファイルなら何も書き換えずに false。
    /// </summary>
    public bool Restore(BackupEntry entry)
    {
        try
        {
            backups.RestoreFrom(entry.FullPath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException
            or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogError(ex, "復元に失敗しました");
            Status = $"復元できませんでした: {ex.Message}";
            return false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };
}

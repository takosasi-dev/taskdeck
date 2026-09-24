using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TaskDeck.App;

/// <summary>
/// データの置き場所。既定は %LOCALAPPDATA%\TaskDeck。
/// 環境変数 TASKDECK_DATA_DIR があればそこを使う（開発・検証用。本番のデータ・レジストリ・ホットキーを汚さない）。
/// </summary>
public sealed record AppPaths(string DataDirectory, bool IsDevelopment)
{
    public const string DataDirVariable = "TASKDECK_DATA_DIR";

    public string DatabasePath => Path.Combine(DataDirectory, "taskdeck.db");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>多重起動防止と起動済みインスタンスへの合図に使う名前。開発時はデータフォルダごとに別になる。</summary>
    public string InstanceKey => IsDevelopment ? "TaskDeck-dev-" + ShortHash(DataDirectory) : "TaskDeck";

    public static AppPaths Resolve()
    {
        var custom = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return new AppPaths(Path.GetFullPath(custom), IsDevelopment: true);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "TaskDeck"), IsDevelopment: false);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string ShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant()));
        return Convert.ToHexString(bytes, 0, 6);
    }
}

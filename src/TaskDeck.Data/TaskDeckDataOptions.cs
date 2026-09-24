using Microsoft.Data.Sqlite;

namespace TaskDeck.Data;

/// <summary>DB とバックアップの置き場所。App の AppPaths から作る（開発時は TASKDECK_DATA_DIR 配下）。</summary>
public sealed record TaskDeckDataOptions(string DatabasePath, string BackupDirectory)
{
    public string ConnectionString => BuildConnectionString(DatabasePath, pooling: true);

    /// <summary>外部キー制約を必ず有効にした接続文字列。バックアップ・復元はプールを使わない接続で行う。</summary>
    public static string BuildConnectionString(string path, bool pooling) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        ForeignKeys = true,
        Pooling = pooling,
    }.ToString();
}

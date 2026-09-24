using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Entities;
using TaskDeck.Data.Backup;

namespace TaskDeck.Data;

/// <param name="BackupDeferred">起動時バックアップを後回しにした（呼び出し側が後で取る）。</param>
public sealed record DatabaseInitResult(bool Created, IReadOnlyList<string> AppliedMigrations, string? BackupPath, bool BackupDeferred = false);

/// <summary>DB の更新（マイグレーション）に失敗し、起動前の状態に戻したことを表す。</summary>
public sealed class DatabaseMigrationException(string message, string? backupPath, Exception inner) : Exception(message, inner)
{
    /// <summary>戻すのに使ったバックアップ（起動時に取ったもの）。</summary>
    public string? BackupPath { get; } = backupPath;
}

/// <summary>
/// 起動時の DB 準備（設計書 2.4 / 9 Phase 0）: 既存 DB ならバックアップ（適用が無ければ後回しにできる）→ Migrate → 失敗したらバックアップから戻して例外。
/// 新規なら作成。毎回 WAL を指定し、SyncMeta の1行を用意する。
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<TaskDeckDbContext> factory,
    BackupService backup,
    TaskDeckDataOptions options,
    ILogger<DatabaseInitializer> logger)
{
    /// <param name="deferBackupWhenCurrent">
    /// true なら、適用するマイグレーションが無いときはバックアップを取らずに返す（<see cref="DatabaseInitResult.BackupDeferred"/>。
    /// 呼び出し側が画面を出した後に <see cref="BackupService.CreateStartupBackup"/> を呼ぶ。起動を速くするため）。
    /// マイグレーションがあるときは、この値によらず更新の前に必ず取る。
    /// </param>
    public async Task<DatabaseInitResult> InitializeAsync(bool deferBackupWhenCurrent = false, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
        var existed = File.Exists(options.DatabasePath);
        string? backupPath = null;

        IReadOnlyList<string> pending;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            pending = [.. await db.Database.GetPendingMigrationsAsync(ct)];
            if (existed && (pending.Count > 0 || !deferBackupWhenCurrent))
            {
                backupPath = backup.CreateStartupBackup();
            }
            if (pending.Count > 0)
            {
                logger.LogInformation("マイグレーションを適用します: {Migrations}", string.Join(", ", pending));
                await db.Database.MigrateAsync(ct);
            }
        }
        catch (Exception ex) when (backupPath is not null && ex is SqliteException or DbUpdateException or InvalidOperationException)
        {
            logger.LogError(ex, "マイグレーションに失敗したため、起動時バックアップから戻します: {Backup}", backupPath);
            SqliteConnection.ClearAllPools();
            BackupService.Copy(backupPath, options.DatabasePath);
            throw new DatabaseMigrationException("データベースの更新に失敗したため、起動前の状態に戻しました。", backupPath, ex);
        }

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            if (!await db.SyncMeta.AnyAsync(ct))
            {
                db.SyncMeta.Add(new SyncMeta());
                await db.SaveChangesAsync(ct);
            }
        }

        logger.LogInformation("DB を準備しました（新規作成={Created}, 適用={Count}件）", !existed, pending.Count);
        return new DatabaseInitResult(!existed, pending, backupPath, BackupDeferred: existed && backupPath is null);
    }
}

using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Maintenance;

/// <summary>
/// 起動時の保守（F-015 / F-107）。削除から30日を超えたゴミ箱を物理削除し、
/// 前回から1か月以上たっていれば VACUUM する。ログに出すのは件数と所要時間だけ（タスク名は出さない）。
/// </summary>
public sealed class DatabaseMaintenance(
    IDbContextFactory<TaskDeckDbContext> factory,
    ITaskRepository tasks,
    IAppStateRepository state,
    IClock clock,
    ILogger<DatabaseMaintenance> logger)
{
    /// <summary>ゴミ箱に置いておく日数（F-015）。</summary>
    public const int TrashRetentionDays = 30;

    public async Task RunStartupAsync(CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var purged = await tasks.PurgeDeletedAsync(clock.UtcNow.AddDays(-TrashRetentionDays), ct);
        logger.LogInformation(
            "ゴミ箱の掃除: {Days} 日を超えた {Count} 件を完全に削除しました（{Elapsed} ms）",
            TrashRetentionDays,
            purged,
            watch.ElapsedMilliseconds);

        var today = clock.LocalToday();
        if (ParseDate(await state.GetAsync(AppStateKeys.LastVacuumDate, ct)) is { } last && today < last.AddMonths(1))
        {
            return;
        }
        var vacuum = Stopwatch.StartNew();
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            // VACUUM はトランザクションの中では実行できないので、ここは素の SQL を1本だけ流す
            await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
        }
        catch (SqliteException ex)
        {
            // 他の接続が掴んでいるなどで失敗しても起動は続ける。最終日を書かないので次の起動でまた試す
            logger.LogWarning(ex, "VACUUM に失敗しました");
            return;
        }
        await state.SetAsync(AppStateKeys.LastVacuumDate, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
        logger.LogInformation("VACUUM を実行しました（{Elapsed} ms）", vacuum.ElapsedMilliseconds);
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}

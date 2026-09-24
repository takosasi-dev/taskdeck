using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Data;
using TaskDeck.Data.Backup;
using TaskDeck.Data.Infrastructure;
using TaskDeck.Data.Repositories;
using TaskDeck.Data.Seeding;
using TaskDeck.Tests.TestSupport;
using Xunit.Abstractions;

namespace TaskDeck.Tests.Data;

/// <summary>
/// 普段のテスト実行を遅くしないよう、環境変数 TASKDECK_PERF=1 のときだけ動く Fact。
/// </summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TASKDECK_PERF") != "1")
        {
            Skip = "性能計測は TASKDECK_PERF=1 のときだけ実行する";
        }
    }
}

/// <summary>
/// 1万件（NFR 8章の想定）での一覧・検索・件数の実測。一時フォルダのファイル DB（本番と同じ接続文字列・WAL）で測る。
/// 実行: TASKDECK_PERF=1 dotnet test --filter FullyQualifiedName~PerformanceTests
/// </summary>
public sealed class PerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const int TaskCount = 10_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taskdeck-perf", Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private sealed class FileDbFactory(DbContextOptions<TaskDeckDbContext> options) : IDbContextFactory<TaskDeckDbContext>
    {
        public TaskDeckDbContext CreateDbContext() => new(options);
    }

    [PerfFact]
    public async Task Query_TenThousandTasks_MeetsBudget()
    {
        Directory.CreateDirectory(_dir);
        var options = new TaskDeckDataOptions(Path.Combine(_dir, "taskdeck.db"), Path.Combine(_dir, "backups"));
        var dbOptions = new DbContextOptionsBuilder<TaskDeckDbContext>()
            .UseSqlite(options.ConnectionString)
            .AddInterceptors(new AuditInterceptor(_clock))
            .Options;
        var factory = new FileDbFactory(dbOptions);
        var backup = new BackupService(options, _clock, NullLogger<BackupService>.Instance);
        await new DatabaseInitializer(factory, backup, options, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        var hub = new DataChangeHub();
        var tasks = new TaskRepository(
            factory,
            _clock,
            Substitute.For<IRecurrenceEngine>(),
            new FakeSettingsStore(),
            hub,
            NullLogger<TaskRepository>.Instance);

        var seedWatch = Stopwatch.StartNew();
        var seeded = await new DevDataSeeder(factory, _clock, hub, NullLogger<DevDataSeeder>.Instance).SeedAsync(TaskCount, 42);
        seedWatch.Stop();
        output.WriteLine($"投入: {seeded} 件 / {seedWatch.ElapsedMilliseconds} ms / DB {new FileInfo(options.DatabasePath).Length / 1024} KB");

        var today = await MeasureAsync("今日ビュー", async () => (await tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.Today))).Count);
        await MeasureAsync(
            "今日ビュー（サブタスクを添えない）",
            async () => (await tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.Today) with { IncludeSubtasks = false })).Count);
        var all = await MeasureAsync("すべてビュー", async () => (await tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.All))).Count);
        var completed = await MeasureAsync("完了済みビュー", async () => (await tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.Completed))).Count);
        var searchOpen = await MeasureAsync(
            "検索「会議」（未完了）",
            async () => (await tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.All) with { SearchText = "会議" })).Count);
        var searchAll = await MeasureAsync(
            "検索「会議」（全状態）",
            async () => (await tasks.QueryAsync(new TaskQuery { Status = TaskStatusFilter.All, SearchText = "会議" })).Count);
        var counts = await MeasureAsync("件数（サイドバー）", async () => (await tasks.GetViewCountsAsync()).AllOpen);
        var facts = await MeasureAsync("振り返りの完了一覧", async () => (await tasks.GetCompletedFactsAsync(null)).Count);

        // NFR 8章: ビュー切替 100ms・検索 200ms。余裕を見て2倍を上限にし、実測値は出力に残す
        Assert.True(today < 200, $"今日ビューが遅い: {today:F1} ms");
        Assert.True(all < 200, $"すべてビューが遅い: {all:F1} ms");
        Assert.True(completed < 400, $"完了済みビューが遅い: {completed:F1} ms");
        Assert.True(searchOpen < 400, $"検索が遅い: {searchOpen:F1} ms");
        Assert.True(searchAll < 400, $"検索が遅い: {searchAll:F1} ms");
        Assert.True(counts < 200, $"件数が遅い: {counts:F1} ms");
        Assert.True(facts < 400, $"完了一覧が遅い: {facts:F1} ms");
    }

    /// <summary>1回ウォームアップしてから5回測り、中央値を返す（ミリ秒）。</summary>
    private async Task<double> MeasureAsync(string label, Func<Task<int>> action, int runs = 5)
    {
        var rows = await action();
        var times = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            await action();
            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        var median = times[times.Count / 2];
        output.WriteLine($"{label}: 中央値 {median:F1} ms（{string.Join(" / ", times.Select(t => t.ToString("F1")))}）… {rows} 件");
        return median;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

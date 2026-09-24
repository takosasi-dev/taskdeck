using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;
using TaskDeck.Data;
using TaskDeck.Data.Infrastructure;

namespace TaskDeck.Tests.TestSupport;

/// <summary>JST（UTC+9、夏時間なし）に固定した時計。マシンのタイムゾーンに左右されない。</summary>
public sealed class FixedClock : IClock
{
    public static readonly TimeZoneInfo Jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    public FixedClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public DateTime UtcNow { get; set; }

    public TimeZoneInfo LocalTimeZone => Jst;

    /// <summary>JST の壁時計時刻で作る。例: FixedClock.AtLocal(2026, 9, 22, 10, 0) は 2026-09-22 01:00Z。</summary>
    public static FixedClock AtLocal(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-9));

    public void Advance(TimeSpan span) => UtcNow = UtcNow.Add(span);

    /// <summary>JST の壁時計時刻を UTC に（テストの期待値を書くため）。</summary>
    public static DateTime LocalToUtc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-9);
}

/// <summary>メモリ上の設定（ファイルに書かない）。</summary>
public sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>祝日を固定で与える。</summary>
public sealed class FixedHolidays(params DateOnly[] dates) : IHolidayProvider
{
    private readonly HashSet<DateOnly> _dates = [.. dates];

    public bool IsHoliday(DateOnly date) => _dates.Contains(date);

    public string? GetName(DateOnly date) => _dates.Contains(date) ? "祝日" : null;
}

/// <summary>
/// SQLite のインメモリ DB（接続を開いたまま共有）に本物のマイグレーションを当てたもの。
/// IDbContextFactory として渡せる。テストごとに new して Dispose する。
/// </summary>
public sealed class TestDatabase : IDbContextFactory<TaskDeckDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TaskDeckDbContext> _options;

    public TestDatabase(IClock clock)
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TaskDeckDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditInterceptor(clock))
            .Options;
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public TaskDeckDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}

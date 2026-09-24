using Microsoft.EntityFrameworkCore;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Settings;

namespace TaskDeck.Data.External;

/// <summary>
/// 天気の問い合わせ（IWeatherProvider）。WeatherDay 表をメモリに読み込んで即答する。取得は <see cref="WeatherUpdater"/>。
/// 次のときは null（カレンダーは天気を出さない）: 設定の「天気を表示する」が OFF・場所が未設定・まだ取っていない日・
/// 表の中身が今の場所のものでない（場所を変えた直後。取った場所は AppStateKeys.WeatherLocationKey に入っている）。
/// オフラインモードでは取ってあった分を返す（通信しないだけ）。
/// </summary>
public sealed class WeatherCache(IDbContextFactory<TaskDeckDbContext> factory, ISettingsStore? settings) : IWeatherProvider
{
    private volatile Snapshot _snapshot = new(null, new Dictionary<DateOnly, WeatherDay>());

    /// <summary>設定を見ない版（常に表の中身で答える）。</summary>
    public WeatherCache(IDbContextFactory<TaskDeckDbContext> factory)
        : this(factory, null)
    {
    }

    /// <summary>WeatherDay 表と、その場所を読み直す。起動時と取得後に呼ぶ。</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.WeatherDays.AsNoTracking().ToListAsync(ct);
        var key = await db.AppState.AsNoTracking()
            .Where(s => s.Key == AppStateKeys.WeatherLocationKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        _snapshot = new Snapshot(key, rows.ToDictionary(d => d.Date));
    }

    public WeatherDay? Get(DateOnly date)
    {
        var snapshot = _snapshot;
        if (settings is not null)
        {
            var external = settings.Current.External;
            if (!external.WeatherEnabled
                || external.WeatherLocation is not { } location
                || WeatherUpdater.LocationKey(location) != snapshot.LocationKey)
            {
                return null;
            }
        }
        return snapshot.Days.GetValueOrDefault(date);
    }

    private sealed record Snapshot(string? LocationKey, IReadOnlyDictionary<DateOnly, WeatherDay> Days);
}

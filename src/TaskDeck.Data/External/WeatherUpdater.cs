using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.External;

/// <summary>
/// 天気の取得（F-195、Open-Meteo の日別予報 16 日ぶん。設計書 4.7.4）。
/// 送るのは小数第2位に丸めた緯度経度だけ（NFR 6.4。ほかのクエリは固定の値）。タイムゾーンも送らない（timezone=auto で場所から決めてもらう）。
/// 前回から6時間たっていれば取り直す。取るたびに表を丸ごと入れ替える（場所を変えたら前の場所の天気は消える）。
/// 取った場所は AppStateKeys.WeatherLocationKey（"34.69,135.50"）に持つ。WeatherCache は今の設定の場所と違えば何も返さない。
/// </summary>
public sealed class WeatherUpdater(
    ExternalHttp http,
    IDbContextFactory<TaskDeckDbContext> factory,
    WeatherCache cache,
    DataChangeHub hub,
    ISettingsStore settings,
    IClock clock,
    ILogger<WeatherUpdater> logger)
{
    public const int ForecastDays = 16;

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>緯度経度の丸め（小数第2位＝約1km。NFR 6.4）。</summary>
    public static double Round(double degrees) => Math.Round(degrees, 2, MidpointRounding.AwayFromZero);

    /// <summary>送る形の緯度経度（丸めて小数2桁。-0.00 にはしない）。</summary>
    public static string Coordinate(double degrees) => (Round(degrees) + 0.0).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>取った天気がどの場所のものか（丸めた緯度経度）。</summary>
    public static string LocationKey(WeatherLocation location) =>
        Coordinate(location.Latitude) + "," + Coordinate(location.Longitude);

    public static Uri RequestUri(WeatherLocation location) => new(
        "https://api.open-meteo.com/v1/forecast"
        + "?latitude=" + Coordinate(location.Latitude)
        + "&longitude=" + Coordinate(location.Longitude)
        + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
        + "&timezone=auto"
        + "&forecast_days=" + ForecastDays.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// 設定が ON で場所があり、前回から maxAge（省略時 6時間）たっていれば（force なら必ず）取る。取れたら true。
    /// 場所を変えた直後は前回が無い扱いなので、すぐ取る。
    /// </summary>
    public async Task<bool> RefreshAsync(bool force, CancellationToken ct = default, TimeSpan? maxAge = null)
    {
        var external = settings.Current.External;
        if (!external.WeatherEnabled || external.WeatherLocation is not { } location)
        {
            return false;
        }
        await _gate.WaitAsync(ct);
        try
        {
            var key = LocationKey(location);
            if (!force && await GetLastFetchedAtAsync(key, ct) is { } last && clock.UtcNow - last < (maxAge ?? MaxAge))
            {
                return false;
            }
            var root = await http.GetJsonAsync(ExternalApi.Weather, RequestUri(location), ct);
            if (root is null)
            {
                return false;
            }
            var days = Parse(root.Value, clock.UtcNow);
            if (days is null)
            {
                logger.LogWarning("天気の応答が想定外の形だったので使いませんでした");
                return false;
            }
            await ReplaceAsync(days, key, ct);
            await cache.ReloadAsync(ct);
            hub.Publish(DataChangeKind.ExternalCache);
            logger.LogInformation("天気を取得しました（{Count} 日ぶん）", days.Count);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>今の設定の場所の天気を最後に取った日時（UTC）。場所が未設定・まだ取っていない・別の場所のものなら null。</summary>
    public async Task<DateTime?> GetLastFetchedAtAsync(CancellationToken ct = default) =>
        settings.Current.External.WeatherLocation is { } location ? await GetLastFetchedAtAsync(LocationKey(location), ct) : null;

    /// <summary>
    /// Open-Meteo の応答（daily の time と各値の配列）を検査して WeatherDay にする。
    /// 配列の長さがそろわない・日付が読めないときは null（全部捨てる）。値が null の日（予報の先の方で起きる）は飛ばす。
    /// </summary>
    internal static IReadOnlyList<WeatherDay>? Parse(JsonElement root, DateTime fetchedAtUtc)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object
            || !TryArray(daily, "time", out var times)
            || !TryArray(daily, "weather_code", out var codes)
            || !TryArray(daily, "temperature_2m_max", out var maxima)
            || !TryArray(daily, "temperature_2m_min", out var minima)
            || !TryArray(daily, "precipitation_probability_max", out var rain))
        {
            return null;
        }
        var count = times.GetArrayLength();
        if (count == 0 || count > 31
            || codes.GetArrayLength() != count || maxima.GetArrayLength() != count
            || minima.GetArrayLength() != count || rain.GetArrayLength() != count)
        {
            return null;
        }

        var days = new List<WeatherDay>(count);
        for (var i = 0; i < count; i++)
        {
            if (times[i].ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(times[i].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return null;
            }
            if (!TryInt(codes[i], out var code) || code is < 0 or > 99
                || !TryDouble(maxima[i], out var max) || !TryDouble(minima[i], out var min))
            {
                continue;
            }
            int? probability = TryInt(rain[i], out var p) ? Math.Clamp(p, 0, 100) : null;
            days.Add(new WeatherDay
            {
                Date = date,
                WeatherCode = code,
                TemperatureMax = max,
                TemperatureMin = min,
                PrecipitationProbability = probability,
                FetchedAt = fetchedAtUtc,
            });
        }
        return days.Count == 0 || days.Select(d => d.Date).Distinct().Count() != days.Count ? null : days;
    }

    private static bool TryArray(JsonElement parent, string name, out JsonElement array) =>
        parent.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array;

    private static bool TryInt(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number)
            || double.IsNaN(number) || number < int.MinValue || number > int.MaxValue)
        {
            return false;
        }
        value = (int)Math.Round(number);
        return true;
    }

    private static bool TryDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value)
            && !double.IsNaN(value) && !double.IsInfinity(value) && value is > -100 and < 100;
    }

    private async Task<DateTime?> GetLastFetchedAtAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var storedKey = await db.AppState.AsNoTracking()
            .Where(s => s.Key == AppStateKeys.WeatherLocationKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (storedKey != key)
        {
            return null;
        }
        // 表は16行ほどなので読んでから比べる
        var fetched = await db.WeatherDays.AsNoTracking().Select(d => d.FetchedAt).ToListAsync(ct);
        return fetched.Count == 0 ? null : fetched.Max();
    }

    /// <summary>表を丸ごと入れ替え、どの場所のものかも同じトランザクションで書く。</summary>
    private async Task ReplaceAsync(IReadOnlyList<WeatherDay> days, string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.WeatherDays.ExecuteDeleteAsync(ct);
        db.WeatherDays.AddRange(days);
        var state = await db.AppState.FirstOrDefaultAsync(s => s.Key == AppStateKeys.WeatherLocationKey, ct);
        if (state is null)
        {
            db.AppState.Add(new AppStateEntry { Key = AppStateKeys.WeatherLocationKey, Value = key });
        }
        else
        {
            state.Value = key;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

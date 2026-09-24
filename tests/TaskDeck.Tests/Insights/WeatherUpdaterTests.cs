using System.Net;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Data.External;

namespace TaskDeck.Tests.Insights;

/// <summary>天気（Open-Meteo）: 丸めた緯度経度だけを送り、6時間ごとに表を入れ替える。設定が OFF・場所が違えば出さない。</summary>
public sealed class WeatherUpdaterTests : IDisposable
{
    private static readonly WeatherLocation Osaka = new("大阪市（大阪府・日本）", 34.69374, 135.50218);

    private readonly ExternalWorld _world = new();

    public WeatherUpdaterTests()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json(ExternalWorld.WeatherJson));
        _world.Settings.Current.External.WeatherEnabled = true;
        _world.Settings.Current.External.WeatherLocation = Osaka;
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task RefreshAsync_Location_SendsOnlyRoundedCoordinates()
    {
        await _world.NewWeatherUpdater().RefreshAsync(force: false);

        var uri = Assert.Single(_world.Handler.Uris);
        Assert.Equal(
            "https://api.open-meteo.com/v1/forecast?latitude=34.69&longitude=135.50"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=16",
            uri.AbsoluteUri);
        Assert.DoesNotContain("34.6937", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("大阪", Uri.UnescapeDataString(uri.AbsoluteUri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_Success_StoresDaysSkippingNullsAndPublishes()
    {
        Assert.True(await _world.NewWeatherUpdater().RefreshAsync(force: false));

        Assert.Equal(2, await _world.CountWeatherDaysAsync());
        var rainy = _world.Weather.Get(new DateOnly(2026, 9, 24));
        Assert.NotNull(rainy);
        Assert.Equal(61, rainy.WeatherCode);
        Assert.Equal(24.1, rainy.TemperatureMax);
        Assert.Equal(19.8, rainy.TemperatureMin);
        Assert.Equal(80, rainy.PrecipitationProbability);
        Assert.Equal(_world.Clock.UtcNow, rainy.FetchedAt);
        Assert.Null(_world.Weather.Get(new DateOnly(2026, 9, 25)));   // 値が欠けた日は入れない
        Assert.Contains(DataChangeKind.ExternalCache, _world.Published);
        Assert.Equal("34.69,135.50", await _world.State.GetAsync(AppStateKeys.WeatherLocationKey));
    }

    [Fact]
    public async Task RefreshAsync_WithinSixHours_SendsNothingUnlessForced()
    {
        var updater = _world.NewWeatherUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Requests.Clear();

        _world.Clock.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59));
        Assert.False(await updater.RefreshAsync(force: false));
        Assert.Empty(_world.Handler.Requests);

        Assert.True(await updater.RefreshAsync(force: true));
        Assert.Single(_world.Handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_AfterSixHours_FetchesAgain()
    {
        var updater = _world.NewWeatherUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Requests.Clear();

        _world.Clock.Advance(TimeSpan.FromHours(6));

        Assert.True(await updater.RefreshAsync(force: false));
        Assert.Single(_world.Handler.Requests);
        Assert.Equal(2, await _world.CountWeatherDaysAsync());
    }

    [Fact]
    public async Task RefreshAsync_LongerMaxAge_WaitsADayWhileRunning()
    {
        var updater = _world.NewWeatherUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Requests.Clear();

        _world.Clock.Advance(TimeSpan.FromHours(23));
        Assert.False(await updater.RefreshAsync(force: false, maxAge: TimeSpan.FromDays(1)));
        Assert.Empty(_world.Handler.Requests);

        _world.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await updater.RefreshAsync(force: false, maxAge: TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task RefreshAsync_LocationChanged_FetchesAtOnceAndHidesOldPlace()
    {
        var updater = _world.NewWeatherUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Requests.Clear();

        _world.Settings.Current.External.WeatherLocation = new WeatherLocation("札幌市", 43.06417, 141.34694);

        // 取り直す前でも、前の場所の天気は出さない
        Assert.Null(_world.Weather.Get(new DateOnly(2026, 9, 23)));
        Assert.True(await updater.RefreshAsync(force: false));
        Assert.Contains("latitude=43.06&longitude=141.35", Assert.Single(_world.Handler.Uris).Query, StringComparison.Ordinal);
        Assert.NotNull(_world.Weather.Get(new DateOnly(2026, 9, 23)));
    }

    [Fact]
    public async Task RefreshAsync_DisabledOrNoLocation_SendsNothing()
    {
        _world.Settings.Current.External.WeatherEnabled = false;
        Assert.False(await _world.NewWeatherUpdater().RefreshAsync(force: true));

        _world.Settings.Current.External.WeatherEnabled = true;
        _world.Settings.Current.External.WeatherLocation = null;
        Assert.False(await _world.NewWeatherUpdater().RefreshAsync(force: true));

        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_Offline_SendsNothing()
    {
        _world.Settings.Current.External.OfflineMode = true;

        Assert.False(await _world.NewWeatherUpdater().RefreshAsync(force: true));
        Assert.Empty(_world.Handler.Requests);
    }

    [Theory]
    [InlineData("""{"daily":{"time":["2026-09-23"],"weather_code":[3]}}""")]
    [InlineData("""{"daily":{"time":["2026-09-23","2026-09-24"],"weather_code":[3],"temperature_2m_max":[1],"temperature_2m_min":[1],"precipitation_probability_max":[1]}}""")]
    [InlineData("""{"daily":{"time":["9/23"],"weather_code":[3],"temperature_2m_max":[1],"temperature_2m_min":[1],"precipitation_probability_max":[1]}}""")]
    [InlineData("""{"error":true,"reason":"Latitude must be in range of -90 to 90°."}""")]
    [InlineData("""[1,2,3]""")]
    public async Task RefreshAsync_UnexpectedShape_KeepsPreviousWeather(string json)
    {
        var updater = _world.NewWeatherUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json(json));

        Assert.False(await updater.RefreshAsync(force: true));
        Assert.Equal(2, await _world.CountWeatherDaysAsync());
    }

    [Fact]
    public async Task RefreshAsync_TooManyRequests_StopsForAnHour()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(HttpStatusCode.TooManyRequests));
        var updater = _world.NewWeatherUpdater();

        Assert.False(await updater.RefreshAsync(force: true));
        Assert.False(await updater.RefreshAsync(force: true));

        Assert.Single(_world.Handler.Requests);
    }

    [Fact]
    public async Task WeatherCache_SettingOffOrNoLocation_ReturnsNull()
    {
        await _world.NewWeatherUpdater().RefreshAsync(force: false);
        var day = new DateOnly(2026, 9, 23);
        Assert.NotNull(_world.Weather.Get(day));

        _world.Settings.Current.External.WeatherEnabled = false;
        Assert.Null(_world.Weather.Get(day));

        _world.Settings.Current.External.WeatherEnabled = true;
        _world.Settings.Current.External.WeatherLocation = null;
        Assert.Null(_world.Weather.Get(day));
    }

    [Fact]
    public async Task WeatherCache_Offline_ReturnsFetchedWeather()
    {
        await _world.NewWeatherUpdater().RefreshAsync(force: false);

        _world.Settings.Current.External.OfflineMode = true;

        Assert.NotNull(_world.Weather.Get(new DateOnly(2026, 9, 23)));
    }

    [Fact]
    public async Task GetLastFetchedAtAsync_ReturnsTimeForCurrentPlaceOnly()
    {
        var updater = _world.NewWeatherUpdater();
        Assert.Null(await updater.GetLastFetchedAtAsync());

        await updater.RefreshAsync(force: false);
        Assert.Equal(_world.Clock.UtcNow, await updater.GetLastFetchedAtAsync());

        _world.Settings.Current.External.WeatherLocation = new WeatherLocation("札幌市", 43.06, 141.35);
        Assert.Null(await updater.GetLastFetchedAtAsync());
    }

    [Theory]
    [InlineData(34.69374, "34.69")]
    [InlineData(141.34694, "141.35")]
    [InlineData(135.50218, "135.50")]
    [InlineData(-33.8651, "-33.87")]
    [InlineData(0.004, "0.00")]
    [InlineData(-0.004, "0.00")]
    public void Coordinate_RoundsToTwoDecimals(double degrees, string expected) =>
        Assert.Equal(expected, WeatherUpdater.Coordinate(degrees));
}

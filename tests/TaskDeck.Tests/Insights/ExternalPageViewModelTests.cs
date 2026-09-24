using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.App.Settings.Pages;
using TaskDeck.App.ViewModels;
using TaskDeck.Data.External;

namespace TaskDeck.Tests.Insights;

/// <summary>UI スレッドの代わりにその場で実行する（振り返り・設定のテスト用）。</summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>設定「外部サービス」の K の部分: 天気の場所を探して選ぶ・今すぐ取得・取得の状況。</summary>
public sealed class ExternalPageViewModelTests : IDisposable
{
    private const string PlacesJson =
        """
        {"results":[
          {"name":"大阪市","latitude":34.69374,"longitude":135.50218,"admin1":"大阪府","country":"日本"},
          {"name":"大阪","latitude":34.6,"longitude":135.4,"country":"日本"}
        ]}
        """;

    private readonly ExternalWorld _world = new();
    private readonly ExternalPageViewModel _viewModel;

    public ExternalPageViewModelTests()
    {
        _world.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "geocoding-api.open-meteo.com" => FakeExternalHandler.Json(PlacesJson),
            "www8.cao.go.jp" => FakeExternalHandler.Csv(ExternalWorld.HolidaysCsv()),
            _ => FakeExternalHandler.Json(ExternalWorld.WeatherJson),
        });
        _viewModel = new ExternalPageViewModel(
            _world.Settings,
            _world.NewHolidayUpdater(),
            _world.NewWeatherUpdater(),
            _world.NewPlaceSearch(),
            _world.Hub,
            new InlineUiDispatcher(),
            _world.Clock,
            NullLogger<ExternalPageViewModel>.Instance);
    }

    public void Dispose()
    {
        _viewModel.Detach();
        _world.Dispose();
    }

    [Fact]
    public async Task SearchPlaces_Name_ListsCandidates()
    {
        _viewModel.PlaceQuery = "大阪";

        await _viewModel.SearchPlacesCommand.ExecuteAsync(null);

        Assert.Equal(["大阪市（大阪府・日本）", "大阪（日本）"], _viewModel.Places.Select(p => p.Label));
        Assert.Null(_viewModel.SearchMessage);
    }

    [Fact]
    public void SearchPlaces_OfflineOrBlank_CannotRun()
    {
        Assert.False(_viewModel.SearchPlacesCommand.CanExecute(null));   // 空欄

        _viewModel.PlaceQuery = "大阪";
        Assert.True(_viewModel.SearchPlacesCommand.CanExecute(null));

        _viewModel.OfflineMode = true;
        Assert.False(_viewModel.SearchPlacesCommand.CanExecute(null));
        Assert.False(_viewModel.RefreshNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task SearchPlaces_ServerDown_SaysItCouldNotSearch()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(HttpStatusCode.ServiceUnavailable));
        _viewModel.PlaceQuery = "大阪";

        await _viewModel.SearchPlacesCommand.ExecuteAsync(null);

        Assert.Empty(_viewModel.Places);
        Assert.StartsWith("探せませんでした", _viewModel.SearchMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPlaces_NoResults_SaysNotFound()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json("""{"generationtime_ms":0.1}"""));
        _viewModel.PlaceQuery = "  どこにもない町 ";

        await _viewModel.SearchPlacesCommand.ExecuteAsync(null);

        Assert.StartsWith("「どこにもない町」は見つかりませんでした。「大阪市」のように", _viewModel.SearchMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectPlace_Candidate_StoresRoundedLocationAndTurnsWeatherOn()
    {
        _viewModel.PlaceQuery = "大阪";
        await _viewModel.SearchPlacesCommand.ExecuteAsync(null);

        _viewModel.SelectPlaceCommand.Execute(_viewModel.Places[0]);

        var location = _world.Settings.Current.External.WeatherLocation;
        Assert.NotNull(location);
        Assert.Equal("大阪市（大阪府・日本）", location.Name);
        Assert.Equal(34.69, location.Latitude);
        Assert.Equal(135.5, location.Longitude);
        Assert.True(_viewModel.WeatherEnabled);
        Assert.Equal("場所: 大阪市（大阪府・日本）", _viewModel.WeatherLocationName);
        Assert.Empty(_viewModel.Places);
        Assert.Equal("", _viewModel.PlaceQuery);
    }

    [Fact]
    public async Task AttachAsync_NothingFetchedYet_ExplainsTheState()
    {
        await _viewModel.AttachAsync();

        Assert.Equal("まだ取得していません", _viewModel.HolidayStatus);
        Assert.Equal("場所を選ぶと取得します", _viewModel.WeatherStatus);
    }

    [Fact]
    public async Task RefreshNow_Success_FetchesBothAndShowsWhen()
    {
        _world.Settings.Current.External.WeatherEnabled = true;
        _world.Settings.Current.External.WeatherLocation = new PlaceCandidate("大阪市", "大阪府", "日本", 34.69, 135.5).ToLocation();

        await _viewModel.RefreshNowCommand.ExecuteAsync(null);

        Assert.Equal("取得しました", _viewModel.RefreshMessage);
        Assert.Equal("2026〜2027年を取得済み（9月23日 10:00）", _viewModel.HolidayStatus);
        Assert.Equal("最後に取得: 9月23日 10:00（16日先まで）", _viewModel.WeatherStatus);
        Assert.False(_viewModel.IsRefreshing);
    }

    [Fact]
    public async Task RefreshNow_WeatherFails_SaysPartlyFailed()
    {
        _world.Settings.Current.External.WeatherEnabled = true;
        _world.Settings.Current.External.WeatherLocation = new PlaceCandidate("大阪市", null, null, 34.69, 135.5).ToLocation();
        _world.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.Host == "www8.cao.go.jp"
            ? FakeExternalHandler.Csv(ExternalWorld.HolidaysCsv())
            : FakeExternalHandler.Status(HttpStatusCode.InternalServerError));

        await _viewModel.RefreshNowCommand.ExecuteAsync(null);

        Assert.StartsWith("一部を取得できませんでした", _viewModel.RefreshMessage, StringComparison.Ordinal);
        Assert.Equal("まだ取得していません", _viewModel.WeatherStatus);
    }

    [Fact]
    public async Task BackgroundFetch_Published_ReloadsStatus()
    {
        await _viewModel.AttachAsync();

        // 裏の取得（ExternalDataWorker）が祝日を取った
        await _world.NewHolidayUpdater().RefreshAsync(force: false);

        Assert.True(await WaitAsync(() => _viewModel.HolidayStatus.StartsWith("2026〜2027年", StringComparison.Ordinal)));
    }

    [Fact]
    public void ValuesOnlyConstructor_HidesFetchParts()
    {
        var valuesOnly = new ExternalPageViewModel(_world.Settings);

        Assert.False(valuesOnly.CanFetch);
        Assert.False(valuesOnly.RefreshNowCommand.CanExecute(null));
        Assert.True(_viewModel.CanFetch);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
        return condition();
    }
}

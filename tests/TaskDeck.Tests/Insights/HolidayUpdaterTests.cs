using System.Net;
using System.Text;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Insights;

/// <summary>祝日（内閣府の CSV）: 1日1回まで取りに行き、表を丸ごと入れ替えてキャッシュを読み直す。送るものは無い。</summary>
public sealed class HolidayUpdaterTests : IDisposable
{
    private readonly ExternalWorld _world = new();

    public HolidayUpdaterTests() =>
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Csv(ExternalWorld.HolidaysCsv()));

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task RefreshAsync_FirstTime_FetchesTheCsvOnceAndReloadsCache()
    {
        Assert.Equal(1, await _world.NewHolidayUpdater().RefreshAsync(force: false));

        Assert.Equal([ExternalWorld.HolidaysCsvUri], _world.Handler.Uris.Select(u => u.AbsoluteUri));
        Assert.Equal(7, await _world.CountHolidaysAsync());
        Assert.Equal("元日", _world.Holidays.GetName(new DateOnly(2026, 1, 1)));
        Assert.True(_world.Holidays.IsHoliday(new DateOnly(2027, 1, 11)));
        Assert.Contains(DataChangeKind.ExternalCache, _world.Published);
    }

    [Fact]
    public async Task RefreshAsync_SubstituteAndCitizensHolidays_StayOnTheirOwnDates()
    {
        await _world.NewHolidayUpdater().RefreshAsync(force: false);

        // Nager.Date では 5/3 が消えて 5/6 が「憲法記念日」になり、9/22 の国民の休日が無かった
        Assert.Equal("憲法記念日", _world.Holidays.GetName(new DateOnly(2026, 5, 3)));
        Assert.Equal("休日", _world.Holidays.GetName(new DateOnly(2026, 5, 6)));
        Assert.Equal("休日", _world.Holidays.GetName(new DateOnly(2026, 9, 22)));
    }

    [Fact]
    public async Task RefreshAsync_AlreadyFetchedToday_SendsNothing()
    {
        var updater = _world.NewHolidayUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Requests.Clear();
        _world.Clock.UtcNow = FixedClock.LocalToUtc(2026, 9, 23, 23, 59);

        Assert.Equal(0, await updater.RefreshAsync(force: false));
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_NextDay_FetchesAgainAndReplaces()
    {
        var updater = _world.NewHolidayUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Requests.Clear();
        _world.Clock.UtcNow = FixedClock.LocalToUtc(2026, 9, 24, 0, 1);

        Assert.Equal(1, await updater.RefreshAsync(force: false));
        Assert.Single(_world.Handler.Requests);
        Assert.Equal(7, await _world.CountHolidaysAsync());   // 入れ替えなので増えない
    }

    [Fact]
    public async Task RefreshAsync_Force_FetchesAgainTheSameDay()
    {
        var updater = _world.NewHolidayUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Requests.Clear();

        Assert.Equal(1, await updater.RefreshAsync(force: true));
        Assert.Single(_world.Handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_RecordFromNagerDate_FetchesAgain()
    {
        // 切り替え前（Nager.Date）の記録が今日の日付でも、取り直して正しい祝日に入れ替える
        await _world.State.SetAsync(AppStateKeys.HolidayFetchedYears, """{"2026":"2026-09-23T00:30:00Z","2027":"2026-09-23T00:30:00Z"}""");

        Assert.Equal(1, await _world.NewHolidayUpdater().RefreshAsync(force: false));
        Assert.Equal("休日", _world.Holidays.GetName(new DateOnly(2026, 9, 22)));
    }

    [Fact]
    public async Task RefreshAsync_Utf8WithBom_IsReadToo()
    {
        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("国民の祝日・休日月日,国民の祝日・休日名称\n2026/1/1,元日\n")).ToArray();
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Csv(utf8));

        Assert.Equal(1, await _world.NewHolidayUpdater().RefreshAsync(force: false));
        Assert.Equal("元日", _world.Holidays.GetName(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public async Task RefreshAsync_Disabled_SendsNothing()
    {
        _world.Settings.Current.External.HolidaysEnabled = false;

        Assert.Equal(0, await _world.NewHolidayUpdater().RefreshAsync(force: true));
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_Offline_SendsNothing()
    {
        _world.Settings.Current.External.OfflineMode = true;

        Assert.Equal(0, await _world.NewHolidayUpdater().RefreshAsync(force: true));
        Assert.Empty(_world.Handler.Requests);
        Assert.Equal(0, await _world.CountHolidaysAsync());
    }

    [Theory]
    [InlineData("<html><body>ただいまメンテナンス中です</body></html>")]
    [InlineData("<html>\r\n<body>ただいまメンテナンス中です</body>\r\n</html>\r\n")]
    [InlineData("国民の祝日・休日月日,国民の祝日・休日名称\r\n2026/1/1\r\n")]
    [InlineData("国民の祝日・休日月日,国民の祝日・休日名称\r\n2026-01-01,元日\r\n")]
    [InlineData("国民の祝日・休日月日,国民の祝日・休日名称\r\n2025/1/1,元日\r\n")]
    [InlineData("国民の祝日・休日月日,国民の祝日・休日名称\r\n")]
    [InlineData("")]
    public async Task RefreshAsync_UnexpectedShape_StoresNothingAndTriesAgainLater(string csv)
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Csv(ExternalWorld.HolidaysCsv(csv)));
        var updater = _world.NewHolidayUpdater();

        Assert.Equal(0, await updater.RefreshAsync(force: false));
        Assert.Equal(0, await _world.CountHolidaysAsync());

        // 取った記録を残していないので、次の回にまた取りに行く
        _world.Handler.Requests.Clear();
        await updater.RefreshAsync(force: false);
        Assert.Single(_world.Handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_ServerDown_KeepsPreviousHolidays()
    {
        var updater = _world.NewHolidayUpdater();
        await updater.RefreshAsync(force: false);
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(HttpStatusCode.InternalServerError));

        Assert.Equal(0, await updater.RefreshAsync(force: true));
        Assert.Equal(7, await _world.CountHolidaysAsync());
        Assert.True(_world.Holidays.IsHoliday(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public async Task GetStatusAsync_AfterFetch_ReturnsTimeAndYears()
    {
        var updater = _world.NewHolidayUpdater();
        var (none, noYears) = await updater.GetStatusAsync();
        Assert.Null(none);
        Assert.Empty(noYears);

        await updater.RefreshAsync(force: false);
        var (last, years) = await updater.GetStatusAsync();

        Assert.Equal(_world.Clock.UtcNow, last);
        Assert.Equal([2026, 2027], years);
        Assert.NotNull(await _world.State.GetAsync(AppStateKeys.HolidayFetchedYears));
    }

    [Fact]
    public async Task HolidayCache_SettingOff_AnswersNoHolidays()
    {
        await _world.NewHolidayUpdater().RefreshAsync(force: false);

        _world.Settings.Current.External.HolidaysEnabled = false;

        Assert.False(_world.Holidays.IsHoliday(new DateOnly(2026, 1, 1)));
        Assert.Null(_world.Holidays.GetName(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public async Task HolidayCache_Offline_StillUsesFetchedHolidays()
    {
        await _world.NewHolidayUpdater().RefreshAsync(force: false);

        _world.Settings.Current.External.OfflineMode = true;

        Assert.True(_world.Holidays.IsHoliday(new DateOnly(2026, 1, 1)));
    }
}

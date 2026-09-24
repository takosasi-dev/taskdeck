using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.App.Residency;
using TaskDeck.Core.Abstractions;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Residency;

/// <summary>トレイ（F-091・F-096、UI 設計書 14.3・23.3）のバッジとメニュー。時計は JST 2026-09-22 10:00。</summary>
public class TrayViewModelTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
    private readonly FakeHotkeyApi _api = new();
    private readonly HotkeyService _hotkeys;
    private readonly List<string> _invoked = [];
    private readonly TrayViewModel _viewModel;

    public TrayViewModelTests()
    {
        _hotkeys = new HotkeyService(_settings, Switches.Production, _api, new InlineUiDispatcher(), NullLogger<HotkeyService>.Instance);
        _viewModel = new TrayViewModel(_settings, _clock, _hotkeys, new TrayActions(
            () => _invoked.Add("open"),
            () => _invoked.Add("quick"),
            () => _invoked.Add("settings"),
            () => _invoked.Add("exit")));
    }

    private static ViewCounts Counts(int today, int overdue) =>
        ViewCounts.Empty with { Today = today, Overdue = overdue, AllOpen = today + 20 };

    [Fact]
    public void StateFor_NothingToday_HasNoBadge() => Assert.Null(_viewModel.StateFor(Counts(0, 0)).Badge);

    [Fact]
    public void StateFor_TodayWithoutOverdue_ShowsTodayCountInAccent()
    {
        var badge = _viewModel.StateFor(Counts(7, 0)).Badge;

        Assert.NotNull(badge);
        Assert.Equal("7", badge.Text);
        Assert.False(badge.IsOverdue);
    }

    [Fact]
    public void StateFor_WithOverdue_ShowsOverdueCountInRed()
    {
        var badge = _viewModel.StateFor(Counts(7, 2)).Badge;

        Assert.NotNull(badge);
        Assert.Equal("2", badge.Text);
        Assert.True(badge.IsOverdue);
    }

    [Theory]
    [InlineData(98, "98")]
    [InlineData(99, "99+")]
    [InlineData(250, "99+")]
    public void BadgeText_LargeCounts_CapAt99Plus(int count, string expected) =>
        Assert.Equal(expected, new TrayBadge(count, false).Text);

    [Fact]
    public void StateFor_Paused_MarksPausedAndSaysUntilWhen()
    {
        _settings.Update(s => s.Notifications.PausedUntilUtc = _clock.UtcNow.AddMinutes(45));

        var state = _viewModel.StateFor(Counts(3, 0));

        Assert.True(state.IsPaused);
        Assert.Contains("10:45 まで", state.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMenu_Default_HasFiveItemsWithoutSync()
    {
        var menu = _viewModel.BuildMenu();

        Assert.Equal(["TaskDeck を開く", "クイック追加", "通知を 1 時間止める", "設定", "終了"], menu.OfType<TrayMenuItem>().Select(i => i.Label));
        Assert.Equal(2, menu.Count(i => i is null));
        Assert.DoesNotContain(menu.OfType<TrayMenuItem>(), i => i.Label.Contains("同期", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildMenu_HotkeysRegistered_ShowsGestures()
    {
        _hotkeys.Start();

        var menu = _viewModel.BuildMenu().OfType<TrayMenuItem>().ToList();

        Assert.Equal("Ctrl+Shift+T", menu[0].Gesture);
        Assert.Equal("Ctrl+Shift+Space", menu[1].Gesture);
    }

    [Fact]
    public void BuildMenu_HotkeyInUse_HidesItsGesture()
    {
        _api.TakenKeys.Add(0x20); // Space を他のアプリが使っている
        _hotkeys.Start();

        var menu = _viewModel.BuildMenu().OfType<TrayMenuItem>().ToList();

        Assert.Null(menu[1].Gesture);
        Assert.Equal("Ctrl+Shift+T", menu[0].Gesture);
    }

    [Fact]
    public void PauseItem_Invoked_PausesForAnHour()
    {
        _viewModel.BuildMenu().OfType<TrayMenuItem>().Single(i => i.Label.StartsWith("通知を", StringComparison.Ordinal)).Invoke();

        Assert.Equal(_clock.UtcNow.AddHours(1), _settings.Current.Notifications.PausedUntilUtc);
    }

    [Fact]
    public void PauseItem_WhilePaused_ResumesInstead()
    {
        _viewModel.PauseForAnHour();

        var item = _viewModel.BuildMenu().OfType<TrayMenuItem>().Single(i => i.Label.StartsWith("通知を", StringComparison.Ordinal));
        item.Invoke();

        Assert.Equal("通知を再開する（11:00 まで停止中）", item.Label);
        Assert.Null(_settings.Current.Notifications.PausedUntilUtc);
    }

    [Fact]
    public void MenuItems_Invoked_CallShellActions()
    {
        foreach (var item in _viewModel.BuildMenu().OfType<TrayMenuItem>().Where(i => !i.Label.StartsWith("通知を", StringComparison.Ordinal)))
        {
            item.Invoke();
        }
        _viewModel.Open();

        Assert.Equal(["open", "quick", "settings", "exit", "open"], _invoked);
    }
}

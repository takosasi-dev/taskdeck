using TaskDeck.App.Settings.Pages;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>設定の「通知」（F-124）。波1は値の保存だけ。時刻は読めたときだけ保存し、読めない間は注意書きを出す。</summary>
public class NotificationsPageViewModelTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly NotificationsPageViewModel _viewModel;

    public NotificationsPageViewModelTests() => _viewModel = new NotificationsPageViewModel(_settings);

    [Fact]
    public void DailySummaryTime_FullWidthDigits_IsNormalizedAndStored()
    {
        _viewModel.DailySummaryTime = "７：３０";

        Assert.Equal(new TimeOnly(7, 30), _settings.Current.Notifications.DailySummaryTime);
        Assert.Equal("07:30", _viewModel.DailySummaryTime);
        Assert.Equal("", _viewModel.TimeProblem);
    }

    [Fact]
    public void DefaultReminderTime_Unreadable_KeepsStoredValueAndShowsProblem()
    {
        _viewModel.DefaultReminderTime = "朝";

        Assert.Equal(new TimeOnly(9, 0), _settings.Current.Notifications.DefaultReminderTime);
        Assert.Equal("朝", _viewModel.DefaultReminderTime);
        Assert.NotEqual("", _viewModel.TimeProblem);
    }

    [Fact]
    public void DefaultReminderTime_FixedAfterProblem_ClearsProblem()
    {
        _viewModel.DefaultReminderTime = "25:99";
        _viewModel.DefaultReminderTime = "10:15";

        Assert.Equal(new TimeOnly(10, 15), _settings.Current.Notifications.DefaultReminderTime);
        Assert.Equal("", _viewModel.TimeProblem);
    }

    [Fact]
    public void SnoozeIndex_SetFirst_StoresFiveMinutes()
    {
        _viewModel.SnoozeIndex = 0;

        Assert.Equal(5, _settings.Current.Notifications.SnoozeMinutes);
    }

    [Fact]
    public void SnoozeIndex_UnknownStoredMinutes_ShowsThirtyMinutes()
    {
        _settings.Update(s => s.Notifications.SnoozeMinutes = 7);

        Assert.Equal("30 分後", _viewModel.SnoozeChoices[_viewModel.SnoozeIndex]);
    }

    [Fact]
    public void Enabled_SetFalse_StoresFalse()
    {
        _viewModel.Enabled = false;

        Assert.False(_settings.Current.Notifications.Enabled);
    }

    [Fact]
    public void OverdueNoticeEnabled_SetFalse_StoresFalse()
    {
        _viewModel.OverdueNoticeEnabled = false;

        Assert.False(_settings.Current.Notifications.OverdueNoticeEnabled);
    }

    [Fact]
    public void TogglePause_NotPaused_PausesForAnHour()
    {
        var clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
        var viewModel = new NotificationsPageViewModel(_settings, clock);

        viewModel.TogglePauseCommand.Execute(null);

        Assert.Equal(clock.UtcNow.AddHours(1), _settings.Current.Notifications.PausedUntilUtc);
        Assert.True(viewModel.IsPaused);
        Assert.StartsWith("11:00 まで止めています", viewModel.PauseDescription, StringComparison.Ordinal);
        Assert.Equal("再開する", viewModel.PauseButtonLabel);
    }

    [Fact]
    public void TogglePause_Paused_Resumes()
    {
        var clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
        _settings.Update(s => s.Notifications.PausedUntilUtc = clock.UtcNow.AddMinutes(20));
        var viewModel = new NotificationsPageViewModel(_settings, clock);

        viewModel.TogglePauseCommand.Execute(null);

        Assert.Null(_settings.Current.Notifications.PausedUntilUtc);
        Assert.Equal("1 時間止める", viewModel.PauseButtonLabel);
    }

    [Fact]
    public void IsPaused_PauseTimePassed_IsFalse()
    {
        var clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
        _settings.Update(s => s.Notifications.PausedUntilUtc = clock.UtcNow.AddMinutes(-1));

        Assert.False(new NotificationsPageViewModel(_settings, clock).IsPaused);
    }

    [Fact]
    public void DevNote_DevelopmentWithoutToasts_ExplainsInAppNotices() =>
        Assert.Contains("TASKDECK_DEV_TOASTS=1", new NotificationsPageViewModel(_settings, null, TaskDeck.Tests.Residency.Switches.Development).DevNote, StringComparison.Ordinal);

    [Fact]
    public void DevNote_Production_IsEmpty() =>
        Assert.False(new NotificationsPageViewModel(_settings, null, TaskDeck.Tests.Residency.Switches.Production).HasDevNote);
}

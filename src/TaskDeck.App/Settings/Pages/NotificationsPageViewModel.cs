using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDeck.App.Residency;
using TaskDeck.Core.Reminders;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Settings.Pages;

/// <summary>
/// 通知（F-124）。担当: 波1-D（値の保存）、波3-H（一時停止と、通知を出す側 ReminderWorker）。
/// 時刻は "HH:mm" の文字列で受け、読めないときは保存せずに注意書きを出す（入力は消さない）。
/// 一時停止はトレイの「通知を 1 時間止める」と同じ（settings.Notifications.PausedUntilUtc）。
/// </summary>
public sealed partial class NotificationsPageViewModel(ISettingsStore settings, IClock? clock = null, ResidencySwitches? switches = null) : ObservableObject
{
    private static readonly int[] SnoozeMinutes = [5, 10, 15, 30, 60];

    private string? _dailySummaryTimeText;
    private string? _defaultReminderTimeText;

    public IReadOnlyList<string> SnoozeChoices { get; } = [.. SnoozeMinutes.Select(m => $"{m} 分後")];

    /// <summary>開発用フォルダで Windows の通知を使わないときの案内（それ以外は空）。</summary>
    public string DevNote => switches is { IsDevelopment: true, ShowToasts: false }
        ? "開発用フォルダで動かしているため、Windows の通知は使わず、画面の下の知らせに出します（TASKDECK_DEV_TOASTS=1 で Windows の通知を使います）。"
        : "";

    public bool HasDevNote => DevNote.Length > 0;

    public bool IsPaused => clock is not null && ReminderPlanner.IsPaused(settings.Current.Notifications, clock);

    public string PauseDescription => IsPaused && clock is not null && settings.Current.Notifications.PausedUntilUtc is { } until
        ? $"{clock.ToLocal(until).ToString("H:mm", CultureInfo.InvariantCulture)} まで止めています。再開すると、止めている間に来た分をまとめて出します"
        : "会議中などに、1時間だけ通知を止めます（トレイのメニューからもできます）";

    public string PauseButtonLabel => IsPaused ? "再開する" : "1 時間止める";

    /// <summary>1時間止める／再開する。</summary>
    [RelayCommand]
    private void TogglePause()
    {
        if (clock is null)
        {
            return;
        }
        var resume = IsPaused;
        settings.Update(s => s.Notifications.PausedUntilUtc = resume ? null : clock.UtcNow + TrayViewModel.PauseLength);
        RefreshPause();
    }

    /// <summary>ページを出したとき（トレイから止めた・止めた時間が過ぎたのを反映する）。</summary>
    public void RefreshPause()
    {
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseDescription));
        OnPropertyChanged(nameof(PauseButtonLabel));
    }

    public bool Enabled
    {
        get => settings.Current.Notifications.Enabled;
        set
        {
            if (value == Enabled)
            {
                return;
            }
            settings.Update(s => s.Notifications.Enabled = value);
            OnPropertyChanged();
        }
    }

    public bool DailySummaryEnabled
    {
        get => settings.Current.Notifications.DailySummaryEnabled;
        set
        {
            if (value == DailySummaryEnabled)
            {
                return;
            }
            settings.Update(s => s.Notifications.DailySummaryEnabled = value);
            OnPropertyChanged();
        }
    }

    /// <summary>日次サマリの時刻（HH:mm）。</summary>
    public string DailySummaryTime
    {
        get => _dailySummaryTimeText ?? Format(settings.Current.Notifications.DailySummaryTime);
        set
        {
            _dailySummaryTimeText = value;
            if (TryParse(value, out var time))
            {
                _dailySummaryTimeText = null;
                settings.Update(s => s.Notifications.DailySummaryTime = time);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimeProblem));
        }
    }

    /// <summary>日付のみの期限に相対通知を付けたときの基準時刻（F-124）。</summary>
    public string DefaultReminderTime
    {
        get => _defaultReminderTimeText ?? Format(settings.Current.Notifications.DefaultReminderTime);
        set
        {
            _defaultReminderTimeText = value;
            if (TryParse(value, out var time))
            {
                _defaultReminderTimeText = null;
                settings.Update(s => s.Notifications.DefaultReminderTime = time);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimeProblem));
        }
    }

    /// <summary>時刻として読めない入力があるときの注意書き（無ければ空）。</summary>
    public string TimeProblem =>
        _dailySummaryTimeText is null && _defaultReminderTimeText is null ? "" : "時刻は 8:00 のように入力してください（読めない間は保存していません）";

    public int SnoozeIndex
    {
        get
        {
            var index = Array.IndexOf(SnoozeMinutes, settings.Current.Notifications.SnoozeMinutes);
            return index < 0 ? 3 : index;
        }
        set
        {
            if (value < 0 || value >= SnoozeMinutes.Length || value == SnoozeIndex)
            {
                return;
            }
            settings.Update(s => s.Notifications.SnoozeMinutes = SnoozeMinutes[value]);
            OnPropertyChanged();
        }
    }

    public bool OverdueNoticeEnabled
    {
        get => settings.Current.Notifications.OverdueNoticeEnabled;
        set
        {
            if (value == OverdueNoticeEnabled)
            {
                return;
            }
            settings.Update(s => s.Notifications.OverdueNoticeEnabled = value);
            OnPropertyChanged();
        }
    }

    private static string Format(TimeOnly time) => time.ToString("HH\\:mm", CultureInfo.InvariantCulture);

    /// <summary>全角で打たれても半角に寄せてから読む（CLAUDE.md 1.7）。</summary>
    private static bool TryParse(string? text, out TimeOnly time) =>
        TimeOnly.TryParse(TextNormalizer.ForName(text), CultureInfo.InvariantCulture, out time);
}

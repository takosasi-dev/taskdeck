using System.Globalization;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Reminders;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Residency;

/// <summary>
/// トレイのバッジ（UI 設計書 14.3・23.3）。期限切れがあれば期限切れの数を赤で、無ければ今日の未完了（期限切れを含む「今日」ビューの件数）を
/// アクセント色で出す。0件なら出さない。99件以上は「99+」。
/// 未完了の全件ではなく今日の件数にしているのは、同時に 100〜300 件を抱える使い方（NFR 3.1）だと全件は常に「99+」になり、見る意味が無くなるため。
/// </summary>
public sealed record TrayBadge(int Count, bool IsOverdue)
{
    public string Text => Count >= 99 ? "99+" : Count.ToString(CultureInfo.InvariantCulture);

    public static TrayBadge? From(ViewCounts counts) =>
        counts.Overdue > 0 ? new TrayBadge(counts.Overdue, IsOverdue: true)
        : counts.Today > 0 ? new TrayBadge(counts.Today, IsOverdue: false)
        : null;
}

/// <summary>トレイのアイコンに描くものとツールチップ。</summary>
public sealed record TrayState(TrayBadge? Badge, bool IsPaused, string ToolTip);

/// <summary>トレイのメニューの1項目。Gesture は右に出すホットキー（登録できていないときは null）。</summary>
public sealed record TrayMenuItem(string Label, string? Gesture, string IconKey, Action Invoke);

/// <summary>トレイのメニューから呼ぶ画面の操作（本物は ShellService。テストでは記録するだけの代わりを渡す）。</summary>
public sealed record TrayActions(Action Open, Action QuickAdd, Action OpenSettings, Action Exit);

/// <summary>
/// トレイ（F-091・F-096、UI 設計書 14.3）の中身。メニューは6項目から同期を除いた5つ
/// （開く／クイック追加／通知を1時間止める／設定／終了）。アイコンの絵とメニューの表示は TrayIconService。
/// </summary>
public sealed class TrayViewModel(ISettingsStore settings, IClock clock, HotkeyService hotkeys, TrayActions actions)
{
    public static readonly TimeSpan PauseLength = TimeSpan.FromHours(1);

    public TrayState StateFor(ViewCounts counts)
    {
        var badge = TrayBadge.From(counts);
        var paused = ReminderPlanner.IsPaused(settings.Current.Notifications, clock);
        var text = counts.Today == 0
            ? "TaskDeck ・ 今日のタスクはありません"
            : counts.Overdue > 0
                ? $"TaskDeck ・ 今日 {counts.Today} 件（期限切れ {counts.Overdue} 件）"
                : $"TaskDeck ・ 今日 {counts.Today} 件";
        if (paused)
        {
            text += $"\n通知を止めています（{PausedUntilText()} まで）";
        }
        return new TrayState(badge, paused, text);
    }

    /// <summary>メニューの並び（null は区切り線）。開くたびに作り直す（停止中の表示とホットキーを今の値にする）。</summary>
    public IReadOnlyList<TrayMenuItem?> BuildMenu()
    {
        var paused = ReminderPlanner.IsPaused(settings.Current.Notifications, clock);
        return
        [
            new TrayMenuItem("TaskDeck を開く", GestureOf(HotkeyAction.ShowMainWindow), "IconMonitor", actions.Open),
            new TrayMenuItem("クイック追加", GestureOf(HotkeyAction.QuickInput), "IconAdd", actions.QuickAdd),
            null,
            paused
                ? new TrayMenuItem($"通知を再開する（{PausedUntilText()} まで停止中）", null, "IconBell", Resume)
                : new TrayMenuItem("通知を 1 時間止める", null, "IconBell", PauseForAnHour),
            null,
            new TrayMenuItem("設定", null, "IconSettings", actions.OpenSettings),
            new TrayMenuItem("終了", null, "IconSignOut", actions.Exit),
        ];
    }

    /// <summary>アイコンのクリック（メイン画面を前に出す）。</summary>
    public void Open() => actions.Open();

    public void PauseForAnHour() => settings.Update(s => s.Notifications.PausedUntilUtc = clock.UtcNow + PauseLength);

    public void Resume() => settings.Update(s => s.Notifications.PausedUntilUtc = null);

    private string? GestureOf(HotkeyAction action) =>
        hotkeys.StateOf(action) == HotkeyState.Registered ? HotkeyService.SettingOf(settings.Current.Hotkeys, action) : null;

    private string PausedUntilText() =>
        settings.Current.Notifications.PausedUntilUtc is { } until
            ? clock.ToLocal(until).ToString("H:mm", CultureInfo.InvariantCulture)
            : "";
}

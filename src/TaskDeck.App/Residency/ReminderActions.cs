using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Residency;

/// <summary>
/// 通知から押された操作（F-083）。「完了にする」は完了＋取り消しの記録（一覧のチェックと同じ文言。繰り返しなら次回の日付をトーストで見せる）、
/// 「N分後に再通知」は RemindAt を今から N 分後へ（NotifiedAt は消える。N はボタンに書いた分数。無ければ settings.Notifications.SnoozeMinutes）、
/// 本文のクリックはメイン画面でそのタスクを見せる（まとめ・今日のまとめ・期限切れは「今日」ビュー）。UI スレッドで呼ぶ。
/// </summary>
public sealed class ReminderActions(
    ITaskRepository tasks,
    UndoService undo,
    ISettingsStore settings,
    IClock clock,
    ShellService shell,
    ILogger<ReminderActions> logger)
{
    public const string Complete = "complete";
    public const string Snooze = "snooze";
    public const string Open = "open";

    /// <summary>minutes は「N分後に再通知」のボタンに書いた分数（通知を出した後で設定を変えても、書いてある時間で出し直す）。</summary>
    public async Task HandleAsync(string? action, Guid? taskId, int? minutes = null)
    {
        switch (action)
        {
            case Complete when taskId is { } id:
                await CompleteAsync(id);
                break;
            case Snooze when taskId is { } id:
                await SnoozeAsync(id, minutes);
                break;
            case Open when taskId is { } id:
                shell.RevealTask(id);
                break;
            default:
                shell.NavigateTo(ViewKey.Today);
                break;
        }
    }

    public async Task CompleteAsync(Guid taskId)
    {
        var task = await tasks.GetAsync(taskId);
        if (task is null || task.IsDeleted || !task.IsOpen)
        {
            return; // 通知を出した後に別の場所で片付けた
        }
        var result = await tasks.SetCompletedAsync([taskId], true);
        if (result.Created.Select(t => t.DueAt).FirstOrDefault(d => d is not null) is { } next)
        {
            undo.Record(result, "完了しました・次回は " + DisplayText.DateWithWeekday(clock.ToLocalDate(next)), UndoKind.Toggle, showToast: true);
        }
        else
        {
            undo.Record(result, DisplayText.Quote(task.Title) + "を完了しました", UndoKind.Toggle, showToast: false);
        }
        logger.LogInformation("通知から完了にしました {TaskId}", taskId);
    }

    public async Task SnoozeAsync(Guid taskId, int? minutes = null)
    {
        var after = minutes is > 0 ? minutes.Value : settings.Current.Notifications.SnoozeMinutes;
        await tasks.SnoozeAsync(taskId, clock.UtcNow.AddMinutes(after));
        logger.LogInformation("{Minutes} 分後に通知し直します {TaskId}", after, taskId);
    }
}

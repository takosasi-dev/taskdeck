using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Residency;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Reminders;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.BackgroundTasks;

/// <summary>
/// 通知（設計書 4.3、F-081〜087）。60秒ごとに期限の来たリマインドを探して出し、NotifiedAt を書く（再起動しても出し直さない F-086）。
/// まとめ方と出すかどうかは Core/Reminders の ReminderPlanner が決める:
/// - 1〜3件は個別、4件以上はまとめて1件。起動直後と一時停止の明けは、溜まった分を2件以上ならまとめて1件（F-087）
/// - 今日のまとめ（F-084）と期限切れ（F-085）は AppState の日付で1日1回。今日のまとめを出した日は、期限切れの数もそこに入るので期限切れの知らせは出さない
/// - 一時停止中（PausedUntilUtc）は何も出さず、NotifiedAt も書かない（明けたら出す）
/// - 通知を切っている（Enabled=false）間は出さずに NotifiedAt だけ書く（入れ直したときに溜まった分が一度に出ないように）
/// UI スレッドには乗らない（DB の読み書きをスレッドプールで回す）。出す先は IReminderNotifier。
/// </summary>
internal sealed class ReminderWorker(
    ITaskRepository tasks,
    IAppStateRepository state,
    ISettingsStore settings,
    IClock clock,
    IReminderNotifier notifier,
    ILogger<ReminderWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>起動の直後は画面を出すのを優先して少し待つ。</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => LoopAsync(stoppingToken), stoppingToken);

    private async Task LoopAsync(CancellationToken ct)
    {
        await Task.Delay(StartupDelay, ct);
        var catchUp = true;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            catchUp = await RunOnceAsync(catchUp, ct);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task<bool> RunOnceAsync(bool catchUp, CancellationToken ct)
    {
#pragma warning disable CA1031 // 裏で回す通知の最上位: DB の失敗もログに残して続ける（次の回でまた試す。ExternalDataWorker と同じ扱い）
        try
        {
            return await TickAsync(catchUp, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "通知の確認に失敗しました");
            return catchUp;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// 1回ぶん。catchUp は止まっている間に溜まった分を出す回か。戻り値は次の回の catchUp。
    /// 「出した」の記録（NotifiedAt・AppState の日付）は出した後に書く。途中で失敗したら何も記録せず、次の回に出し直す
    /// （取りこぼしの方が、まれな二重の通知より困るため）。
    /// </summary>
    internal async Task<bool> TickAsync(bool catchUp, CancellationToken ct = default)
    {
        var notifications = settings.Current.Notifications;
        if (ReminderPlanner.IsPaused(notifications, clock))
        {
            return true;
        }
        var now = clock.UtcNow;
        // 1日1回の記録は、判定したときの日付で書く（見回りの途中で日付が変わっても翌日の分を消さない）
        var today = clock.ToLocalDate(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var notices = new List<ReminderNotice>();
        var due = await tasks.GetDueRemindersAsync(now, ct);
        if (due.Count > 0 && notifications.Enabled)
        {
            notices.AddRange(ReminderPlanner.PlanDue(due, catchUp, clock));
        }
        var stamps = await DailyAsync(notifications, notices, ct);
        notifier.Show(notices);
        if (due.Count > 0)
        {
            await tasks.MarkNotifiedAsync([.. due.Select(t => t.Id)], now, ct);
        }
        foreach (var key in stamps)
        {
            await state.SetAsync(key, today, ct);
        }
        return false;
    }

    /// <summary>今日のまとめと期限切れを notices に足し、出したら今日の日付を書く AppState のキーを返す。</summary>
    private async Task<List<string>> DailyAsync(NotificationSettings notifications, List<ReminderNotice> notices, CancellationToken ct)
    {
        var stamps = new List<string>(2);
        var summaryDue = ReminderPlanner.IsDailySummaryDue(notifications, await ReadDateAsync(AppStateKeys.LastDailySummaryDate, ct), clock);
        var overdueDue = ReminderPlanner.IsOverdueNoticeDue(notifications, await ReadDateAsync(AppStateKeys.LastOverdueNoticeDate, ct), clock);
        if (!summaryDue && !overdueDue)
        {
            return stamps;
        }
        var today = await TodayTasksAsync(ct);
        var overdue = today.Where(t => TaskRules.IsOverdue(t, clock)).ToList();
        if (summaryDue)
        {
            stamps.Add(AppStateKeys.LastDailySummaryDate);
            if (ReminderPlanner.DailySummary(today, clock) is { } summary)
            {
                notices.Add(summary);
                if (overdue.Count > 0)
                {
                    stamps.Add(AppStateKeys.LastOverdueNoticeDate); // まとめに期限切れの数が入っている
                    overdueDue = false;
                }
            }
        }
        if (overdueDue && ReminderPlanner.OverdueNotice(overdue) is { } notice)
        {
            notices.Add(notice);
            stamps.Add(AppStateKeys.LastOverdueNoticeDate);
        }
        return stamps;
    }

    /// <summary>「今日」ビューの未完了（期限切れを含む、期限順）。サブタスクも1件と数え、添えるだけの行は含めない。</summary>
    private async Task<IReadOnlyList<TaskItem>> TodayTasksAsync(CancellationToken ct)
    {
        var rows = await tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.Today) with { IncludeSubtasks = false }, ct);
        return [.. rows.Where(r => !r.IsContext).Select(r => r.Task)];
    }

    private async Task<DateOnly?> ReadDateAsync(string key, CancellationToken ct) =>
        DateOnly.TryParseExact(await state.GetAsync(key, ct), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}

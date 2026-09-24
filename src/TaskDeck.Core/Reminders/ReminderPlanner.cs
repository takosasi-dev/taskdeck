using System.Globalization;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.Core.Reminders;

/// <summary>通知の形（UI 設計書 14.1 の3形＋期限切れ F-085）。</summary>
public enum ReminderNoticeKind
{
    /// <summary>リマインド1件（ボタン「完了にする」「N分後に再通知」）。</summary>
    Single,
    /// <summary>同時に4件以上、または止まっている間に溜まった分（まとめて1件）。</summary>
    Batch,
    /// <summary>今日のまとめ（F-084）。</summary>
    DailySummary,
    /// <summary>期限切れがあるときの1日1回（F-085）。</summary>
    Overdue,
}

/// <summary>
/// 通知1件ぶん。Title と Body はトーストとアプリ内の知らせにそのまま出す文（タスク名を含むのでログには出さない）。
/// TaskIds は完了・再通知・ログ用。
/// </summary>
public sealed record ReminderNotice(ReminderNoticeKind Kind, string Title, string Body, IReadOnlyList<Guid> TaskIds)
{
    /// <summary>アプリ内の知らせ（1行）に出す文。</summary>
    public string InAppText => Kind == ReminderNoticeKind.Single
        ? $"「{Title}」{Body}"
        : $"{Title}。{Body.Replace('\n', ' ')}";
}

/// <summary>
/// 通知のまとめ方と、出すかどうかの判定（設計書 4.3、UI 設計書 14）。画面にも DB にも触らない。
/// - 期限の来たリマインドは 1〜3件なら個別、4件以上ならまとめて1件（設計書 4.3）
/// - 止まっている間（起動直後・一時停止の明け）に溜まった分は、2件以上ならまとめて1件（F-087）。1件なら個別と同じ形
/// - 今日のまとめと期限切れは1日1回。どちらも「今日のまとめの時刻」より前には出さない（夜中の 0 時に期限切れを知らせない）
/// </summary>
public static class ReminderPlanner
{
    /// <summary>これより多いとまとめる（4件以上でまとめ）。</summary>
    public const int MaxIndividual = 3;

    /// <summary>まとめの本文に名前を並べる件数（残りは「ほか N 件」）。</summary>
    public const int NamesShown = 3;

    /// <summary>本文に並べるタスク名の長さ（書記素）。トーストの幅 364px に収める。</summary>
    public const int NameMaxLength = 20;

    public static bool IsPaused(NotificationSettings settings, IClock clock) =>
        settings.PausedUntilUtc is { } until && until > clock.UtcNow;

    /// <summary>期限の来たリマインドを通知の並びにする。catchUp は止まっている間に溜まった分を出すとき。</summary>
    public static IReadOnlyList<ReminderNotice> PlanDue(IReadOnlyList<TaskItem> due, bool catchUp, IClock clock)
    {
        if (due.Count == 0)
        {
            return [];
        }
        var batch = catchUp ? due.Count > 1 : due.Count > MaxIndividual;
        return batch ? [Batch(due)] : [.. due.Select(t => Single(t, clock))];
    }

    public static ReminderNotice Single(TaskItem task, IClock clock) =>
        new(ReminderNoticeKind.Single, task.Title, DueText(task, clock), [task.Id]);

    public static ReminderNotice Batch(IReadOnlyList<TaskItem> tasks) =>
        new(ReminderNoticeKind.Batch, $"{tasks.Count} 件のタスクが期限です", Names(tasks), [.. tasks.Select(t => t.Id)]);

    /// <summary>今日のまとめを出す時か（有効・今日まだ出していない・時刻を過ぎた・止めていない）。</summary>
    public static bool IsDailySummaryDue(NotificationSettings settings, DateOnly? lastSent, IClock clock) =>
        settings is { Enabled: true, DailySummaryEnabled: true } && IsMorningCheckDue(settings, lastSent, clock);

    /// <summary>期限切れの知らせを出す時か（有効・今日まだ出していない・今日のまとめの時刻を過ぎた・止めていない）。</summary>
    public static bool IsOverdueNoticeDue(NotificationSettings settings, DateOnly? lastSent, IClock clock) =>
        settings is { Enabled: true, OverdueNoticeEnabled: true } && IsMorningCheckDue(settings, lastSent, clock);

    /// <summary>
    /// 今日のまとめ（「今日は 7 件です」「うち期限切れ 2 件、時刻ありが 3 件。最初の予定は 10:00 の定例」）。
    /// todayTasks は「今日」ビューの未完了（期限切れを含む）。0件なら出さない（null）。
    /// </summary>
    public static ReminderNotice? DailySummary(IReadOnlyList<TaskItem> todayTasks, IClock clock)
    {
        if (todayTasks.Count == 0)
        {
            return null;
        }
        var now = clock.UtcNow;
        var overdue = todayTasks.Count(t => TaskRules.IsOverdue(t, clock));
        var timed = todayTasks.Count(t => t.DueHasTime && !TaskRules.IsOverdue(t, clock));
        var counts = new List<string>(2);
        if (overdue > 0)
        {
            counts.Add($"うち期限切れ {overdue} 件");
        }
        if (timed > 0)
        {
            counts.Add($"時刻ありが {timed} 件");
        }
        var lines = new List<string>(2);
        if (counts.Count > 0)
        {
            lines.Add(string.Join("、", counts) + "。");
        }
        var first = todayTasks
            .Where(t => t.DueHasTime && t.DueAt >= now)
            .OrderBy(t => t.DueAt)
            .FirstOrDefault();
        if (first is not null)
        {
            lines.Add($"最初の予定は {Time(first.DueAt!.Value, clock)} の{Shorten(first.Title)}。");
        }
        var body = lines.Count > 0 ? string.Join("\n", lines) : Names(todayTasks);
        return new ReminderNotice(ReminderNoticeKind.DailySummary, $"今日は {todayTasks.Count} 件です", body, [.. todayTasks.Select(t => t.Id)]);
    }

    /// <summary>期限切れの知らせ（「期限切れのタスクが 3 件あります」）。0件なら null。</summary>
    public static ReminderNotice? OverdueNotice(IReadOnlyList<TaskItem> overdue) =>
        overdue.Count == 0
            ? null
            : new ReminderNotice(ReminderNoticeKind.Overdue, $"期限切れのタスクが {overdue.Count} 件あります", Names(overdue), [.. overdue.Select(t => t.Id)]);

    /// <summary>
    /// 個別の通知の本文（「15:00 まで・あと 1 時間」「今日まで」「15:00 を過ぎています」）。
    /// 期限の無いタスク（通知日時だけ決めたもの）は「リマインダー」。
    /// </summary>
    public static string DueText(TaskItem task, IClock clock)
    {
        if (task.DueAt is not { } due)
        {
            return "リマインダー";
        }
        var today = clock.LocalToday();
        var day = clock.ToLocalDate(due);
        if (!task.DueHasTime)
        {
            return (day.DayNumber - today.DayNumber) switch
            {
                0 => "今日まで",
                1 => "明日まで",
                < 0 => $"{day.Month}/{day.Day} まで（期限切れ）",
                _ => $"{day.Month}/{day.Day} まで",
            };
        }
        var when = (day.DayNumber - today.DayNumber) switch
        {
            0 => Time(due, clock),
            1 => "明日 " + Time(due, clock),
            _ => $"{day.Month}/{day.Day} {Time(due, clock)}",
        };
        var left = due - clock.UtcNow;
        return left > TimeSpan.Zero ? $"{when} まで・あと {Span(left)}" : $"{when} を過ぎています";
    }

    /// <summary>「45 分」「1 時間 30 分」「5 時間」「2 日」。3時間以上は分を落とす。</summary>
    public static string Span(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        if (minutes < 60)
        {
            return $"{minutes} 分";
        }
        if (minutes < 24 * 60)
        {
            var (hours, rest) = Math.DivRem(minutes, 60);
            return rest > 0 && hours < 3 ? $"{hours} 時間 {rest} 分" : $"{hours} 時間";
        }
        return $"{minutes / (24 * 60)} 日";
    }

    private static bool IsMorningCheckDue(NotificationSettings settings, DateOnly? lastSent, IClock clock)
    {
        if (IsPaused(settings, clock))
        {
            return false;
        }
        var now = clock.LocalNow();
        return lastSent != DateOnly.FromDateTime(now) && TimeOnly.FromDateTime(now) >= settings.DailySummaryTime;
    }

    /// <summary>「会議資料まとめる、AWS課題、買い出し、ほか 1 件」。</summary>
    private static string Names(IReadOnlyList<TaskItem> tasks)
    {
        var names = string.Join("、", tasks.Take(NamesShown).Select(t => Shorten(t.Title)));
        return tasks.Count > NamesShown ? $"{names}、ほか {tasks.Count - NamesShown} 件" : names;
    }

    private static string Shorten(string title) =>
        TextNormalizer.GraphemeCount(title) > NameMaxLength ? TextNormalizer.TruncateGraphemes(title, NameMaxLength) + "…" : title;

    private static string Time(DateTime utc, IClock clock) => clock.ToLocal(utc).ToString("H:mm", CultureInfo.InvariantCulture);
}

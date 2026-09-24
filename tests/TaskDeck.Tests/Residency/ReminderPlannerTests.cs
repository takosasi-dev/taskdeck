using TaskDeck.Core.Entities;
using TaskDeck.Core.Reminders;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Residency;

/// <summary>通知のまとめ方と、出すかどうかの判定（設計書 4.3、UI 設計書 14、F-084〜087）。時計は JST 2026-09-22（火）10:00。</summary>
public class ReminderPlannerTests
{
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private static TaskItem Task(string title, DateTime? due = null, bool hasTime = true) =>
        new() { Title = title, DueAt = due, DueHasTime = due is not null && hasTime };

    private static List<TaskItem> Tasks(int count) =>
        [.. Enumerable.Range(1, count).Select(i => Task($"タスク{i}", FixedClock.LocalToUtc(2026, 9, 22, 10, 0)))];

    [Fact]
    public void PlanDue_NoTasks_ReturnsNothing() =>
        Assert.Empty(ReminderPlanner.PlanDue([], catchUp: false, _clock));

    [Fact]
    public void PlanDue_OneTask_ReturnsSingle()
    {
        var notices = ReminderPlanner.PlanDue(Tasks(1), catchUp: false, _clock);

        var notice = Assert.Single(notices);
        Assert.Equal(ReminderNoticeKind.Single, notice.Kind);
        Assert.Equal("タスク1", notice.Title);
    }

    [Fact]
    public void PlanDue_ThreeTasks_ReturnsThreeSingles()
    {
        var notices = ReminderPlanner.PlanDue(Tasks(3), catchUp: false, _clock);

        Assert.Equal(3, notices.Count);
        Assert.All(notices, n => Assert.Equal(ReminderNoticeKind.Single, n.Kind));
    }

    [Fact]
    public void PlanDue_FourTasks_ReturnsOneBatchWithThreeNames()
    {
        var tasks = Tasks(4);

        var notice = Assert.Single(ReminderPlanner.PlanDue(tasks, catchUp: false, _clock));

        Assert.Equal(ReminderNoticeKind.Batch, notice.Kind);
        Assert.Equal("4 件のタスクが期限です", notice.Title);
        Assert.Equal("タスク1、タスク2、タスク3、ほか 1 件", notice.Body);
        Assert.Equal(tasks.Select(t => t.Id), notice.TaskIds);
    }

    [Fact]
    public void PlanDue_CatchUpTwoTasks_ReturnsOneBatch()
    {
        var notice = Assert.Single(ReminderPlanner.PlanDue(Tasks(2), catchUp: true, _clock));

        Assert.Equal(ReminderNoticeKind.Batch, notice.Kind);
        Assert.Equal("タスク1、タスク2", notice.Body);
    }

    [Fact]
    public void PlanDue_CatchUpOneTask_ReturnsSingle() =>
        Assert.Equal(ReminderNoticeKind.Single, Assert.Single(ReminderPlanner.PlanDue(Tasks(1), catchUp: true, _clock)).Kind);

    [Fact]
    public void Batch_LongTitle_IsShortened()
    {
        var notice = ReminderPlanner.Batch([Task(new string('あ', 30)), Task("短い")]);

        Assert.Equal(new string('あ', 20) + "…、短い", notice.Body);
    }

    [Fact]
    public void DueText_TimedLaterToday_ShowsTimeAndRemaining() =>
        Assert.Equal("15:00 まで・あと 5 時間",
            ReminderPlanner.DueText(Task("会議", FixedClock.LocalToUtc(2026, 9, 22, 15, 0)), _clock));

    [Fact]
    public void DueText_TimedWithinTwoHours_ShowsMinutesToo() =>
        Assert.Equal("11:30 まで・あと 1 時間 30 分",
            ReminderPlanner.DueText(Task("会議", FixedClock.LocalToUtc(2026, 9, 22, 11, 30)), _clock));

    [Fact]
    public void DueText_TimedPast_SaysPassed() =>
        Assert.Equal("9:30 を過ぎています",
            ReminderPlanner.DueText(Task("会議", FixedClock.LocalToUtc(2026, 9, 22, 9, 30)), _clock));

    [Fact]
    public void DueText_TimedTomorrow_SaysTomorrow() =>
        Assert.Equal("明日 9:00 まで・あと 23 時間",
            ReminderPlanner.DueText(Task("会議", FixedClock.LocalToUtc(2026, 9, 23, 9, 0)), _clock));

    [Fact]
    public void DueText_DateOnlyToday_SaysToday() =>
        Assert.Equal("今日まで", ReminderPlanner.DueText(Task("提出", _clock.LocalDayStartUtc(new DateOnly(2026, 9, 22)), hasTime: false), _clock));

    [Fact]
    public void DueText_NoDue_SaysReminder() =>
        Assert.Equal("リマインダー", ReminderPlanner.DueText(Task("電話"), _clock));

    [Fact]
    public void IsDailySummaryDue_AfterTimeNotSentToday_IsTrue() =>
        Assert.True(ReminderPlanner.IsDailySummaryDue(new NotificationSettings(), new DateOnly(2026, 9, 21), _clock));

    [Fact]
    public void IsDailySummaryDue_AlreadySentToday_IsFalse() =>
        Assert.False(ReminderPlanner.IsDailySummaryDue(new NotificationSettings(), new DateOnly(2026, 9, 22), _clock));

    [Fact]
    public void IsDailySummaryDue_BeforeTime_IsFalse() =>
        Assert.False(ReminderPlanner.IsDailySummaryDue(new NotificationSettings { DailySummaryTime = new TimeOnly(10, 1) }, null, _clock));

    [Fact]
    public void IsDailySummaryDue_Disabled_IsFalse()
    {
        Assert.False(ReminderPlanner.IsDailySummaryDue(new NotificationSettings { DailySummaryEnabled = false }, null, _clock));
        Assert.False(ReminderPlanner.IsDailySummaryDue(new NotificationSettings { Enabled = false }, null, _clock));
    }

    [Fact]
    public void IsDailySummaryDue_Paused_IsFalse() =>
        Assert.False(ReminderPlanner.IsDailySummaryDue(new NotificationSettings { PausedUntilUtc = _clock.UtcNow.AddMinutes(30) }, null, _clock));

    [Fact]
    public void IsPaused_PauseExpired_IsFalse() =>
        Assert.False(ReminderPlanner.IsPaused(new NotificationSettings { PausedUntilUtc = _clock.UtcNow.AddMinutes(-1) }, _clock));

    [Fact]
    public void IsOverdueNoticeDue_BeforeMorningTime_IsFalse()
    {
        var midnight = FixedClock.AtLocal(2026, 9, 22, 0, 5);

        Assert.False(ReminderPlanner.IsOverdueNoticeDue(new NotificationSettings(), null, midnight));
    }

    [Fact]
    public void IsOverdueNoticeDue_AfterMorningNotSentToday_IsTrue() =>
        Assert.True(ReminderPlanner.IsOverdueNoticeDue(new NotificationSettings { DailySummaryEnabled = false }, null, _clock));

    [Fact]
    public void DailySummary_Mixed_CountsOverdueTimedAndFirstPlan()
    {
        List<TaskItem> today =
        [
            Task("請求書", _clock.LocalDayStartUtc(new DateOnly(2026, 9, 21)), hasTime: false),
            Task("朝会", FixedClock.LocalToUtc(2026, 9, 22, 9, 0)),
            Task("定例ミーティング", FixedClock.LocalToUtc(2026, 9, 22, 11, 0)),
            Task("会議資料", FixedClock.LocalToUtc(2026, 9, 22, 15, 0)),
            Task("買い物", _clock.LocalDayStartUtc(new DateOnly(2026, 9, 22)), hasTime: false),
        ];

        var notice = ReminderPlanner.DailySummary(today, _clock);

        Assert.NotNull(notice);
        Assert.Equal("今日は 5 件です", notice.Title);
        Assert.Equal("うち期限切れ 2 件、時刻ありが 2 件。\n最初の予定は 11:00 の定例ミーティング。", notice.Body);
    }

    [Fact]
    public void DailySummary_NoTasks_ReturnsNull() => Assert.Null(ReminderPlanner.DailySummary([], _clock));

    [Fact]
    public void OverdueNotice_FiveTasks_ListsThreeAndRest()
    {
        var notice = ReminderPlanner.OverdueNotice(Tasks(5));

        Assert.NotNull(notice);
        Assert.Equal("期限切れのタスクが 5 件あります", notice.Title);
        Assert.EndsWith("ほか 2 件", notice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void InAppText_DailySummary_IsOneLine()
    {
        var notice = ReminderPlanner.DailySummary([Task("定例", FixedClock.LocalToUtc(2026, 9, 22, 11, 0))], _clock);

        Assert.NotNull(notice);
        Assert.Equal("今日は 1 件です。時刻ありが 1 件。 最初の予定は 11:00 の定例。", notice.InAppText);
    }

    [Fact]
    public void InAppText_Single_QuotesTitle() =>
        Assert.Equal("「会議」15:00 まで・あと 5 時間",
            ReminderPlanner.Single(Task("会議", FixedClock.LocalToUtc(2026, 9, 22, 15, 0)), _clock).InAppText);
}

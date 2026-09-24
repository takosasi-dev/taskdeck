using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.App.BackgroundTasks;
using TaskDeck.App.Residency;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Reminders;
using TaskDeck.Tests.Data;

namespace TaskDeck.Tests.Residency;

/// <summary>
/// 通知の見回り（設計書 4.3、F-081〜087）。本物のリポジトリ（インメモリ SQLite）に、記録するだけの通知先をつなぐ。
/// 時計は JST 2026-09-22（火）10:00。
/// </summary>
public class ReminderWorkerTests : DataTestBase
{
    private readonly RecordingNotifier _notifier = new();
    private readonly ReminderWorker _worker;

    public ReminderWorkerTests()
    {
        _worker = new ReminderWorker(Tasks, State, Settings, Clock, _notifier, NullLogger<ReminderWorker>.Instance);
        // リマインドの確認では、今日のまとめと期限切れは切っておく（それぞれのテストで入れる）
        Settings.Update(s =>
        {
            s.Notifications.DailySummaryEnabled = false;
            s.Notifications.OverdueNoticeEnabled = false;
        });
    }

    private async Task<Guid> ReminderAsync(string title, DateTime? remindAt = null)
    {
        var result = await Tasks.AddAsync(new NewTaskRequest
        {
            Title = title,
            DueAt = At(9, 22, 11),
            DueHasTime = true,
            RemindAt = remindAt ?? Clock.UtcNow.AddMinutes(-1),
        });
        return result.Created[0].Id;
    }

    private async Task AddRemindersAsync(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            await ReminderAsync($"タスク{i}");
        }
    }

    [Fact]
    public async Task TickAsync_NothingDue_ShowsNothing()
    {
        await ReminderAsync("まだ先", Clock.UtcNow.AddMinutes(10));

        await _worker.TickAsync(catchUp: false);

        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task TickAsync_OneDue_ShowsSingleAndMarksNotified()
    {
        var id = await ReminderAsync("会議資料");

        await _worker.TickAsync(catchUp: false);

        var notice = Assert.Single(_notifier.Shown);
        Assert.Equal(ReminderNoticeKind.Single, notice.Kind);
        Assert.Equal("会議資料", notice.Title);
        Assert.Equal(Clock.UtcNow, (await GetAsync(id)).NotifiedAt);
    }

    [Fact]
    public async Task TickAsync_ThreeDue_ShowsThreeSingles()
    {
        await AddRemindersAsync(3);

        await _worker.TickAsync(catchUp: false);

        Assert.Equal(3, _notifier.Shown.Count);
        Assert.All(_notifier.Shown, n => Assert.Equal(ReminderNoticeKind.Single, n.Kind));
    }

    [Fact]
    public async Task TickAsync_FourDue_ShowsOneBatch()
    {
        await AddRemindersAsync(4);

        await _worker.TickAsync(catchUp: false);

        var notice = Assert.Single(_notifier.Shown);
        Assert.Equal(ReminderNoticeKind.Batch, notice.Kind);
        Assert.Equal(4, notice.TaskIds.Count);
    }

    [Fact]
    public async Task TickAsync_CatchUpAfterStartup_BatchesTwo()
    {
        await AddRemindersAsync(2);

        var next = await _worker.TickAsync(catchUp: true);

        Assert.Equal(ReminderNoticeKind.Batch, Assert.Single(_notifier.Shown).Kind);
        Assert.False(next);
    }

    [Fact]
    public async Task TickAsync_AlreadyNotified_DoesNotRepeat()
    {
        await ReminderAsync("一度だけ");
        await _worker.TickAsync(catchUp: false);

        Clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.TickAsync(catchUp: false);

        Assert.Single(_notifier.Shown);
    }

    [Fact]
    public async Task TickAsync_Paused_ShowsNothingAndLeavesForLater()
    {
        var id = await ReminderAsync("会議中に来た");
        Settings.Update(s => s.Notifications.PausedUntilUtc = Clock.UtcNow.AddMinutes(30));

        var catchUp = await _worker.TickAsync(catchUp: false);

        Assert.Empty(_notifier.Shown);
        Assert.Null((await GetAsync(id)).NotifiedAt);
        Assert.True(catchUp);
    }

    [Fact]
    public async Task TickAsync_PauseEnded_ShowsWhatCameDuringPauseTogether()
    {
        await AddRemindersAsync(2);
        Settings.Update(s => s.Notifications.PausedUntilUtc = Clock.UtcNow.AddMinutes(30));
        var catchUp = await _worker.TickAsync(catchUp: false);

        Clock.Advance(TimeSpan.FromMinutes(31));
        await _worker.TickAsync(catchUp);

        Assert.Equal(ReminderNoticeKind.Batch, Assert.Single(_notifier.Shown).Kind);
    }

    [Fact]
    public async Task TickAsync_NotificationsOff_MarksWithoutShowing()
    {
        var id = await ReminderAsync("出さない");
        Settings.Update(s => s.Notifications.Enabled = false);

        await _worker.TickAsync(catchUp: false);

        Assert.Empty(_notifier.Shown);
        Assert.NotNull((await GetAsync(id)).NotifiedAt);
    }

    [Fact]
    public async Task TickAsync_DailySummary_ShownOncePerDay()
    {
        Settings.Update(s => s.Notifications.DailySummaryEnabled = true);
        await AddAsync("今日のタスク", Day(9, 22));

        await _worker.TickAsync(catchUp: false);
        await _worker.TickAsync(catchUp: false);
        Clock.Advance(TimeSpan.FromDays(1));
        await _worker.TickAsync(catchUp: false);

        Assert.Equal(2, _notifier.Shown.Count(n => n.Kind == ReminderNoticeKind.DailySummary));
        Assert.Equal("2026-09-23", await State.GetAsync(AppStateKeys.LastDailySummaryDate));
    }

    [Fact]
    public async Task TickAsync_BeforeSummaryTime_WaitsForTheTime()
    {
        Settings.Update(s =>
        {
            s.Notifications.DailySummaryEnabled = true;
            s.Notifications.DailySummaryTime = new TimeOnly(11, 0);
        });
        await AddAsync("今日のタスク", Day(9, 22));

        await _worker.TickAsync(catchUp: false);
        Assert.Empty(_notifier.Shown);

        Clock.Advance(TimeSpan.FromHours(1));
        await _worker.TickAsync(catchUp: false);
        Assert.Equal(ReminderNoticeKind.DailySummary, Assert.Single(_notifier.Shown).Kind);
    }

    [Fact]
    public async Task TickAsync_OverdueOnly_ShownOncePerDay()
    {
        Settings.Update(s => s.Notifications.OverdueNoticeEnabled = true);
        await AddAsync("昨日の分", Day(9, 21));

        await _worker.TickAsync(catchUp: false);
        await _worker.TickAsync(catchUp: false);

        var notice = Assert.Single(_notifier.Shown);
        Assert.Equal(ReminderNoticeKind.Overdue, notice.Kind);
        Assert.Equal("期限切れのタスクが 1 件あります", notice.Title);
    }

    [Fact]
    public async Task TickAsync_SummaryCoversOverdue_DoesNotSendOverdueSeparately()
    {
        Settings.Update(s =>
        {
            s.Notifications.DailySummaryEnabled = true;
            s.Notifications.OverdueNoticeEnabled = true;
        });
        await AddAsync("昨日の分", Day(9, 21));

        await _worker.TickAsync(catchUp: false);
        await _worker.TickAsync(catchUp: false);

        var notice = Assert.Single(_notifier.Shown);
        Assert.Equal(ReminderNoticeKind.DailySummary, notice.Kind);
        Assert.Contains("うち期限切れ 1 件", notice.Body, StringComparison.Ordinal);
        Assert.Equal("2026-09-22", await State.GetAsync(AppStateKeys.LastOverdueNoticeDate));
    }

    [Fact]
    public async Task TickAsync_DayChangesDuringTick_StampsTheDayItJudged()
    {
        Settings.Update(s => s.Notifications.DailySummaryEnabled = true);
        await AddAsync("今日のタスク", Day(9, 22));
        var notifier = new DayChangingNotifier(Clock);
        var worker = new ReminderWorker(Tasks, State, Settings, Clock, notifier, NullLogger<ReminderWorker>.Instance);

        await worker.TickAsync(catchUp: false);

        Assert.Equal("2026-09-22", await State.GetAsync(AppStateKeys.LastDailySummaryDate));
    }

    /// <summary>出している間に日付が変わる（23:59 の見回りの途中でスリープに入った等）。</summary>
    private sealed class DayChangingNotifier(TaskDeck.Tests.TestSupport.FixedClock clock) : IReminderNotifier
    {
        public void Show(IReadOnlyList<ReminderNotice> notices) => clock.Advance(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task TickAsync_NotifierFails_LeavesReminderForNextTick()
    {
        var id = await ReminderAsync("出せなかった");
        var failing = new ReminderWorker(Tasks, State, Settings, Clock, new FailingNotifier(), NullLogger<ReminderWorker>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.TickAsync(catchUp: false));

        Assert.Null((await GetAsync(id)).NotifiedAt);
    }

    private sealed class FailingNotifier : IReminderNotifier
    {
        public void Show(IReadOnlyList<ReminderNotice> notices) => throw new InvalidOperationException("通知を出せない");
    }

    [Fact]
    public async Task TickAsync_NoOverdue_KeepsOverdueNoticeForLaterToday()
    {
        Settings.Update(s => s.Notifications.OverdueNoticeEnabled = true);
        await AddAsync("午後の会議", At(9, 22, 13), dueHasTime: true);

        await _worker.TickAsync(catchUp: false);
        Assert.Empty(_notifier.Shown);

        Clock.Advance(TimeSpan.FromHours(4)); // 14:00 に期限切れになる
        await _worker.TickAsync(catchUp: false);
        Assert.Equal(ReminderNoticeKind.Overdue, Assert.Single(_notifier.Shown).Kind);
    }
}

using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;

namespace TaskDeck.Tests.Data;

public sealed class TaskRepositoryReadTests : DataTestBase
{
    [Fact]
    public async Task GetCompletedFacts_CancelledAndDeleted_AreNotCounted()
    {
        var done = await AddAsync("完了", Day(9, 20));
        var cancelled = await AddAsync("中止");
        var deleted = await AddAsync("削除");
        await Tasks.SetCompletedAsync([done.Id], true);
        await Tasks.UpdateAsync(cancelled.Id, t => t.Status = TaskItemStatus.Cancelled);
        await Tasks.SetCompletedAsync([deleted.Id], true);
        await Tasks.SoftDeleteAsync([deleted.Id]);

        var facts = await Tasks.GetCompletedFactsAsync(null);

        var fact = Assert.Single(facts);
        Assert.Equal(done.Id, fact.Id);
        Assert.Equal(Clock.UtcNow, fact.CompletedAt);
        Assert.Equal(Day(9, 20), fact.DueAt);
    }

    [Fact]
    public async Task GetCompletedFacts_WithFrom_ReturnsNewerOnlyInClosedOrder()
    {
        var old = await AddAsync("先月");
        await Tasks.SetCompletedAsync([old.Id], true);
        Clock.Advance(TimeSpan.FromDays(30));
        var recent = await AddAsync("今月");
        await Tasks.SetCompletedAsync([recent.Id], true);

        var all = await Tasks.GetCompletedFactsAsync(null);
        var since = await Tasks.GetCompletedFactsAsync(Clock.UtcNow.AddDays(-1));

        Assert.Equal([old.Id, recent.Id], all.Select(f => f.Id));
        Assert.Equal([recent.Id], since.Select(f => f.Id));
    }

    [Fact]
    public async Task GetDueReminders_OnlyOpenUnnotified_OrdersByRemindAt()
    {
        var soon = await AddAsync("近い", At(9, 22, 12), dueHasTime: true, remindOffsetMinutes: 60);   // 11:00
        var earlier = await AddAsync("もっと近い", At(9, 22, 11), dueHasTime: true, remindOffsetMinutes: 60);  // 10:00
        var future = await AddAsync("まだ先", At(9, 23, 12), dueHasTime: true, remindOffsetMinutes: 60);
        var notified = await AddAsync("通知済み", At(9, 22, 11), dueHasTime: true, remindOffsetMinutes: 60);
        var closed = await AddAsync("完了済み", At(9, 22, 11), dueHasTime: true, remindOffsetMinutes: 60);
        await Tasks.MarkNotifiedAsync([notified.Id], Clock.UtcNow);
        await Tasks.SetCompletedAsync([closed.Id], true);

        // 10:30 の時点で通知すべきなのは 10:00 のものだけ（11:00 のものはまだ先）
        var due = await Tasks.GetDueRemindersAsync(At(9, 22, 10, 30));

        Assert.Equal([earlier.Id], due.Select(t => t.Id));
        Assert.DoesNotContain(due, t => t.Id == soon.Id || t.Id == future.Id);
    }

    [Fact]
    public async Task GetInDueRange_IncludeClosed_AddsCompletedTasks()
    {
        var open = await AddAsync("期間内", Day(9, 23));
        var done = await AddAsync("期間内で完了", Day(9, 24));
        var outside = await AddAsync("期間外", Day(9, 30));
        await Tasks.SetCompletedAsync([done.Id], true);

        var openOnly = await Tasks.GetInDueRangeAsync(Day(9, 23), Day(9, 25), includeClosed: false);
        var withClosed = await Tasks.GetInDueRangeAsync(Day(9, 23), Day(9, 25), includeClosed: true);

        Assert.Equal([open.Id], openOnly.Select(t => t.Id));
        Assert.Equal([open.Id, done.Id], withClosed.Select(t => t.Id));
        Assert.DoesNotContain(withClosed, t => t.Id == outside.Id);
    }

    [Fact]
    public async Task GetOpenRecurring_ReturnsOpenTasksWithLiveRule()
    {
        var repeating = await AddAsync("毎週", Day(9, 22), recurrence: new RecurrenceInput(RecurrencePresets.Weekly([DayOfWeek.Tuesday])));
        var released = await AddAsync("解除する", Day(9, 22), recurrence: new RecurrenceInput(RecurrencePresets.Daily()));
        var plain = await AddAsync("繰り返しなし", Day(9, 22));
        await Tasks.SetRecurrenceAsync(released.Id, null);

        var open = await Tasks.GetOpenRecurringAsync();

        var info = Assert.Single(open);
        Assert.Equal(repeating.Id, info.Task.Id);
        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", info.Rule.RRule);
        Assert.DoesNotContain(open, i => i.Task.Id == plain.Id);
    }

    [Fact]
    public async Task Query_CompletedView_ReturnsClosedTasksNewestFirst()
    {
        var first = await AddAsync("先に閉じた");
        var second = await AddAsync("後に閉じた");
        var open = await AddAsync("未完了");
        await Tasks.SetCompletedAsync([first.Id], true);
        Clock.Advance(TimeSpan.FromHours(1));
        await Tasks.UpdateAsync(second.Id, t => t.Status = TaskItemStatus.Cancelled);

        var rows = await Tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.Completed));

        Assert.Equal([second.Id, first.Id], rows.Select(r => r.Task.Id));
        Assert.DoesNotContain(rows, r => r.Task.Id == open.Id);
    }

    [Fact]
    public async Task Query_TwoTags_ReturnsOnlyTasksHavingBoth()
    {
        var both = await AddAsync("両方", tagNames: ["仕事", "急ぎ"]);
        await AddAsync("片方", tagNames: ["仕事"]);
        var tags = await Tags.GetAllAsync();
        var ids = tags.Select(t => t.Id).ToList();

        var rows = await Tasks.QueryAsync(new TaskQuery { TagIds = ids });

        var row = Assert.Single(rows);
        Assert.Equal(both.Id, row.Task.Id);
        Assert.Equal(2, row.TagIds.Count);
    }

    [Fact]
    public async Task Query_ParentWithChildren_CountsDoneSubtasks()
    {
        var parent = await AddAsync("親", Day(9, 22));
        var child1 = await AddAsync("子1", parentId: parent.Id);
        await AddAsync("子2", parentId: parent.Id);
        await Tasks.SetCompletedAsync([child1.Id], true);

        var rows = await Tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.Today));

        var row = rows.Single(r => r.Task.Id == parent.Id);
        Assert.Equal(2, row.SubtaskCount);
        Assert.Equal(1, row.SubtaskDoneCount);
        Assert.All(rows.Where(r => r.Task.Id != parent.Id), r => Assert.True(r.IsContext));
    }

    [Fact]
    public async Task GetDetail_ReturnsTagsRuleAndLiveChildren()
    {
        var parent = await AddAsync("親", Day(9, 22), tagNames: ["仕事"], recurrence: new RecurrenceInput(RecurrencePresets.Daily()));
        var child = await AddAsync("子", parentId: parent.Id);
        var removed = await AddAsync("消した子", parentId: parent.Id);
        await Tasks.SoftDeleteAsync([removed.Id]);

        var detail = await Tasks.GetDetailAsync(parent.Id);

        Assert.NotNull(detail);
        Assert.Single(detail.TagIds);
        Assert.Equal("FREQ=DAILY", detail.Rule!.RRule);
        Assert.Equal([child.Id], detail.Children.Select(c => c.Id));
    }

    [Fact]
    public async Task GetViewCounts_CountsByProjectAndTag()
    {
        await AddAsync("今日", Day(9, 22), projectName: "業務改善", tagNames: ["仕事"]);
        await AddAsync("期限切れ", Day(9, 21), projectName: "業務改善");
        await AddAsync("来週", Day(9, 30), tagNames: ["仕事"]);
        var trashed = await AddAsync("ゴミ箱");
        await Tasks.SoftDeleteAsync([trashed.Id]);

        var counts = await Tasks.GetViewCountsAsync();

        Assert.Equal(2, counts.Today);
        Assert.Equal(1, counts.Overdue);
        Assert.Equal(1, counts.Upcoming);
        Assert.Equal(3, counts.AllOpen);
        Assert.Equal(1, counts.Trash);
        Assert.Equal(2, Assert.Single(counts.ByProject).Value);
        Assert.Equal(2, Assert.Single(counts.ByTag).Value);
    }
}

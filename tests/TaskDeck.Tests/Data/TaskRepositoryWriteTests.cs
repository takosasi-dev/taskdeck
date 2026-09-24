using Microsoft.EntityFrameworkCore;
using NSubstitute;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;

namespace TaskDeck.Tests.Data;

public sealed class TaskRepositoryWriteTests : DataTestBase
{
    [Fact]
    public async Task SkipOccurrence_WithNextDue_MovesDueWithoutCompleting()
    {
        Engine.NextDueForSkip(Arg.Any<TaskItem>(), Arg.Any<RecurrenceRule>()).Returns(Day(9, 29));
        var task = await AddAsync(
            "ゴミ出し",
            Day(9, 22),
            remindOffsetMinutes: 60,
            recurrence: new RecurrenceInput(RecurrencePresets.Weekly([DayOfWeek.Tuesday])));

        var result = await Tasks.SkipOccurrenceAsync(task.Id);

        var moved = await GetAsync(task.Id);
        Assert.Equal(Day(9, 29), moved.DueAt);
        Assert.Equal(TaskItemStatus.NotStarted, moved.Status);
        Assert.Null(moved.CompletedAt);
        Assert.Equal(At(9, 29, 8), moved.RemindAt);   // 相対通知は新しい期限から計算し直す（既定 9:00 の1時間前）

        await Tasks.ApplyUndoAsync(result.Changes);
        var back = await GetAsync(task.Id);
        Assert.Equal(Day(9, 22), back.DueAt);
        Assert.Equal(At(9, 22, 8), back.RemindAt);
    }

    [Fact]
    public async Task SkipOccurrence_NoNextDue_ChangesNothing()
    {
        Engine.NextDueForSkip(Arg.Any<TaskItem>(), Arg.Any<RecurrenceRule>()).Returns((DateTime?)null);
        var task = await AddAsync("終わった繰り返し", Day(9, 22), recurrence: new RecurrenceInput(RecurrencePresets.Daily()));

        var result = await Tasks.SkipOccurrenceAsync(task.Id);

        Assert.True(result.Changes.IsEmpty);
        Assert.Equal(Day(9, 22), (await GetAsync(task.Id)).DueAt);
    }

    [Fact]
    public async Task AddTags_KeepsExistingTags_AndUndoRemovesOnlyAdded()
    {
        var work = await Tags.GetOrCreateAsync("仕事");
        var urgent = await Tags.GetOrCreateAsync("急ぎ");
        var withWork = await AddAsync("既に仕事タグ", tagNames: ["仕事"]);
        var plain = await AddAsync("タグなし");

        var result = await Tasks.AddTagsAsync([withWork.Id, plain.Id], [work.Id, urgent.Id]);

        Assert.Equal(2, result.Affected.Count);
        Assert.Equal(2, (await Tasks.GetDetailAsync(withWork.Id))!.TagIds.Count);
        Assert.Equal(2, (await Tasks.GetDetailAsync(plain.Id))!.TagIds.Count);

        await Tasks.ApplyUndoAsync(result.Changes);
        Assert.Equal([work.Id], (await Tasks.GetDetailAsync(withWork.Id))!.TagIds);
        Assert.Empty((await Tasks.GetDetailAsync(plain.Id))!.TagIds);
    }

    [Fact]
    public async Task AddTags_DeletedTag_IsIgnored()
    {
        var gone = await Tags.GetOrCreateAsync("消えるタグ");
        await Tags.DeleteAsync(gone.Id);
        var task = await AddAsync("対象");

        var result = await Tasks.AddTagsAsync([task.Id], [gone.Id]);

        Assert.True(result.Changes.IsEmpty);
        Assert.Empty((await Tasks.GetDetailAsync(task.Id))!.TagIds);
    }

    [Fact]
    public async Task SetRecurrence_New_CreatesRule_AndUndoRemovesIt()
    {
        var task = await AddAsync("掃除", Day(9, 22));

        var result = await Tasks.SetRecurrenceAsync(task.Id, new RecurrenceInput(RecurrencePresets.Daily(), RecurrenceBaseKind.CompletedDate));

        var saved = await GetAsync(task.Id);
        Assert.NotNull(saved.RecurrenceRuleId);
        Assert.Equal(task.Id, saved.RecurrenceSeriesId);
        var detail = await Tasks.GetDetailAsync(task.Id);
        Assert.Equal(RecurrenceBaseKind.CompletedDate, detail!.Rule!.BaseKind);
        Assert.Equal(Day(9, 22), detail.Rule.AnchorAt);

        await Tasks.ApplyUndoAsync(result.Changes);
        Assert.Null((await GetAsync(task.Id)).RecurrenceRuleId);
        await using var db = Db.CreateDbContext();
        Assert.NotNull((await db.RecurrenceRules.SingleAsync()).DeletedAt);   // 作ったルールは無かったことにする
    }

    [Fact]
    public async Task SetRecurrence_Null_ReleasesRuleButKeepsTask()
    {
        var task = await AddAsync("毎日", Day(9, 22), recurrence: new RecurrenceInput(RecurrencePresets.Daily()));

        var result = await Tasks.SetRecurrenceAsync(task.Id, null);

        Assert.Null((await GetAsync(task.Id)).RecurrenceRuleId);
        Assert.Null((await Tasks.GetDetailAsync(task.Id))!.Rule);
        await using (var db = Db.CreateDbContext())
        {
            Assert.NotNull((await db.RecurrenceRules.SingleAsync()).DeletedAt);
        }

        await Tasks.ApplyUndoAsync(result.Changes);
        Assert.NotNull((await GetAsync(task.Id)).RecurrenceRuleId);
        Assert.NotNull((await Tasks.GetDetailAsync(task.Id))!.Rule);
    }

    [Fact]
    public async Task SetRecurrence_Existing_UpdatesRuleInPlace()
    {
        var task = await AddAsync("毎日", Day(9, 22), recurrence: new RecurrenceInput(RecurrencePresets.Daily()));
        var ruleId = (await GetAsync(task.Id)).RecurrenceRuleId;

        var result = await Tasks.SetRecurrenceAsync(
            task.Id,
            new RecurrenceInput(RecurrencePresets.Weekly([DayOfWeek.Monday]), EndKind: RecurrenceEndKind.Count, MaxOccurrences: 5));

        var rule = (await Tasks.GetDetailAsync(task.Id))!.Rule!;
        Assert.Equal(ruleId, rule.Id);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", rule.RRule);
        Assert.Equal(5, rule.MaxOccurrences);

        await Tasks.ApplyUndoAsync(result.Changes);
        var restored = (await Tasks.GetDetailAsync(task.Id))!.Rule!;
        Assert.Equal("FREQ=DAILY", restored.RRule);
        Assert.Equal(RecurrenceEndKind.Never, restored.EndKind);
        Assert.Null(restored.MaxOccurrences);
    }

    [Fact]
    public async Task SetParent_MovesSubtree_AndUndoPutsItBack()
    {
        var target = await AddAsync("移動先");
        var moving = await AddAsync("移動するタスク");
        var child = await AddAsync("その子", parentId: moving.Id);

        var result = await Tasks.SetParentAsync(moving.Id, target.Id, null);

        Assert.True(result.Succeeded);
        var moved = await GetAsync(moving.Id);
        Assert.Equal(target.Id, moved.ParentTaskId);
        Assert.Equal(1, moved.Depth);
        Assert.Equal(2, (await GetAsync(child.Id)).Depth);

        await Tasks.ApplyUndoAsync(result.Value!.Changes);
        Assert.Null((await GetAsync(moving.Id)).ParentTaskId);
        Assert.Equal(0, (await GetAsync(moving.Id)).Depth);
        Assert.Equal(1, (await GetAsync(child.Id)).Depth);
    }

    [Fact]
    public async Task SetParent_WouldMakeFourLevels_Fails()
    {
        var root = await AddAsync("親");
        var second = await AddAsync("子", parentId: root.Id);
        var moving = await AddAsync("移動元");
        await AddAsync("移動元の子", parentId: moving.Id);

        var result = await Tasks.SetParentAsync(moving.Id, second.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal("サブタスクは3階層までです。", result.Error);
        Assert.Null((await GetAsync(moving.Id)).ParentTaskId);
    }

    [Fact]
    public async Task SetParent_OwnDescendant_Fails()
    {
        var parent = await AddAsync("親");
        var child = await AddAsync("子", parentId: parent.Id);

        var result = await Tasks.SetParentAsync(parent.Id, child.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal("自分のサブタスクの下には移動できません。", result.Error);
    }

    [Fact]
    public async Task SetParent_DeletedParent_Fails()
    {
        var parent = await AddAsync("ゴミ箱の親");
        var task = await AddAsync("タスク");
        await Tasks.SoftDeleteAsync([parent.Id]);

        var result = await Tasks.SetParentAsync(task.Id, parent.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal("移動先のタスクが見つかりません。", result.Error);
    }

    [Fact]
    public async Task SetParent_WithoutSortOrder_PutsAtEndOfNewSiblings()
    {
        var parent = await AddAsync("親");
        await AddAsync("先にいる子", parentId: parent.Id);
        var moving = await AddAsync("あとから来る");

        var result = await Tasks.SetParentAsync(moving.Id, parent.Id, null);

        Assert.True(result.Succeeded);
        var children = (await Tasks.GetDetailAsync(parent.Id))!.Children;
        Assert.Equal(["先にいる子", "あとから来る"], children.Select(c => c.Title));
        Assert.Equal(2 * SortOrderMath.Step, children[1].SortOrder);
    }

    [Fact]
    public async Task Reorder_GapTooSmall_RenumbersSiblings()
    {
        var a = await AddAsync("a");
        var b = await AddAsync("b");
        var c = await AddAsync("c");

        var result = await Tasks.ReorderAsync(c.Id, a.SortOrder + 1e-9);   // a のすぐ後ろだが隙間がない

        var rows = await Tasks.QueryAsync(new TaskQuery { SortKey = TaskSortKey.Manual });
        Assert.Equal([a.Id, c.Id, b.Id], rows.Select(r => r.Task.Id));
        Assert.Equal([1024d, 2048d, 3072d], rows.Select(r => r.Task.SortOrder));

        await Tasks.ApplyUndoAsync(result.Changes);
        var back = await Tasks.QueryAsync(new TaskQuery { SortKey = TaskSortKey.Manual });
        Assert.Equal([a.Id, b.Id, c.Id], back.Select(r => r.Task.Id));
    }

    [Fact]
    public async Task Duplicate_WithSubtasks_CopiesAfterOriginalAsNotStarted()
    {
        await AddAsync("前");
        var src = await AddAsync("元のタスク", Day(9, 22), tagNames: ["仕事"]);
        await AddAsync("元の子", parentId: src.Id);
        var last = await AddAsync("後");
        await Tasks.SetCompletedAsync([src.Id], true);

        var result = await Tasks.DuplicateAsync(src.Id, includeSubtasks: true);

        Assert.Equal(2, result.Created.Count);
        var copy = result.Created.Single(t => t.ParentTaskId is null);
        Assert.Equal("元のタスク", copy.Title);
        Assert.Equal(TaskItemStatus.NotStarted, copy.Status);
        Assert.Null(copy.CompletedAt);
        Assert.Equal(Day(9, 22), copy.DueAt);
        Assert.True(copy.SortOrder > src.SortOrder && copy.SortOrder < last.SortOrder);
        var detail = await Tasks.GetDetailAsync(copy.Id);
        Assert.Single(detail!.TagIds);
        Assert.Equal(["元の子"], detail.Children.Select(c => c.Title));

        await Tasks.ApplyUndoAsync(result.Changes);
        Assert.Null(await Tasks.GetAsync(copy.Id));
    }

    [Fact]
    public async Task Duplicate_Recurring_GetsItsOwnRuleAndSeries()
    {
        var src = await AddAsync("毎日", Day(9, 22), recurrence: new RecurrenceInput(RecurrencePresets.Daily()));

        var result = await Tasks.DuplicateAsync(src.Id, includeSubtasks: false);

        var copy = Assert.Single(result.Created);
        Assert.NotEqual(src.RecurrenceRuleId, copy.RecurrenceRuleId);
        Assert.Equal(copy.Id, copy.RecurrenceSeriesId);
        await using var db = Db.CreateDbContext();
        Assert.Equal(2, await db.RecurrenceRules.CountAsync(r => r.DeletedAt == null));
    }

    [Fact]
    public async Task Purge_OnlyTrashedTasks_AreRemoved()
    {
        var live = await AddAsync("生きている");
        var trashed = await AddAsync("ゴミ箱", tagNames: ["仕事"]);
        await Tasks.SoftDeleteAsync([trashed.Id]);

        var count = await Tasks.PurgeAsync([live.Id, trashed.Id]);

        Assert.Equal(1, count);
        Assert.Null(await Tasks.GetAsync(trashed.Id));
        Assert.NotNull(await Tasks.GetAsync(live.Id));
        await using var db = Db.CreateDbContext();
        Assert.Empty(await db.TaskTags.Where(tt => tt.TaskId == trashed.Id).ToListAsync());
    }

    [Fact]
    public async Task PurgeDeleted_WithCutoff_KeepsRecentTrash()
    {
        var old = await AddAsync("古いゴミ");
        await Tasks.SoftDeleteAsync([old.Id]);
        Clock.Advance(TimeSpan.FromDays(40));
        var recent = await AddAsync("最近のゴミ");
        await Tasks.SoftDeleteAsync([recent.Id]);

        var purged = await Tasks.PurgeDeletedAsync(Clock.UtcNow.AddDays(-30));

        Assert.Equal(1, purged);
        Assert.Null(await Tasks.GetAsync(old.Id));
        Assert.NotNull(await Tasks.GetAsync(recent.Id));
        Assert.Equal(1, await Tasks.PurgeDeletedAsync(null));   // null はゴミ箱を空にする
        Assert.Null(await Tasks.GetAsync(recent.Id));
    }

    [Fact]
    public async Task CarryOverOverdue_MovesOverdueToTodayWithoutTime()
    {
        var yesterday = await AddAsync("昨日の分", Day(9, 21), remindOffsetMinutes: 60);
        var thisMorning = await AddAsync("今朝の分", At(9, 22, 9), dueHasTime: true);
        var tonight = await AddAsync("今夜", At(9, 22, 20), dueHasTime: true);
        var tomorrow = await AddAsync("明日", Day(9, 23));
        var closed = await AddAsync("期限切れだが完了", Day(9, 20));
        await Tasks.SetCompletedAsync([closed.Id], true);

        var result = await Tasks.CarryOverOverdueAsync();

        Assert.Equal(2, result.Affected.Count);
        var moved = await GetAsync(yesterday.Id);
        Assert.Equal(Day(9, 22), moved.DueAt);
        Assert.False(moved.DueHasTime);
        Assert.Equal(At(9, 22, 8), moved.RemindAt);
        Assert.Equal(Day(9, 22), (await GetAsync(thisMorning.Id)).DueAt);
        Assert.Equal(At(9, 22, 20), (await GetAsync(tonight.Id)).DueAt);
        Assert.Equal(Day(9, 23), (await GetAsync(tomorrow.Id)).DueAt);

        await Tasks.ApplyUndoAsync(result.Changes);
        Assert.Equal(Day(9, 21), (await GetAsync(yesterday.Id)).DueAt);
        Assert.True((await GetAsync(thisMorning.Id)).DueHasTime);
    }

    [Fact]
    public async Task Snooze_SetsRemindAt_AndClearsOffsetAndNotified()
    {
        var task = await AddAsync("通知つき", At(9, 22, 11), dueHasTime: true, remindOffsetMinutes: 60);
        await Tasks.MarkNotifiedAsync([task.Id], Clock.UtcNow);
        Assert.Equal(Clock.UtcNow, (await GetAsync(task.Id)).NotifiedAt);

        await Tasks.SnoozeAsync(task.Id, At(9, 22, 11, 30));

        var snoozed = await GetAsync(task.Id);
        Assert.Equal(At(9, 22, 11, 30), snoozed.RemindAt);
        Assert.Null(snoozed.RemindOffsetMinutes);
        Assert.Null(snoozed.NotifiedAt);
    }

    [Fact]
    public async Task MarkNotified_SnoozedAfterRead_KeepsTheNewReminder()
    {
        var task = await AddAsync("通知つき", At(9, 22, 11), dueHasTime: true, remindOffsetMinutes: 60);   // 10:00
        var due = await Tasks.GetDueRemindersAsync(Clock.UtcNow);
        await Tasks.SnoozeAsync(task.Id, At(9, 22, 10, 30));   // 読んだ後、書く前に「30分後」が押された

        Assert.True((await Tasks.MarkNotifiedAsync([.. due.Select(t => t.Id)], Clock.UtcNow)).Changes.IsEmpty);
        Assert.Null((await GetAsync(task.Id)).NotifiedAt);
        Assert.Equal([task.Id], (await Tasks.GetDueRemindersAsync(At(9, 22, 10, 30))).Select(t => t.Id));
    }

    [Fact]
    public async Task Writes_DeletedTask_AreIgnored()
    {
        var task = await AddAsync("ゴミ箱のタスク", Day(9, 22), tagNames: ["仕事"]);
        await Tasks.SoftDeleteAsync([task.Id]);
        var tag = (await Tags.GetAllAsync())[0];

        Assert.True((await Tasks.UpdateAsync(task.Id, t => t.Title = "書き換え")).Changes.IsEmpty);
        Assert.True((await Tasks.SetCompletedAsync([task.Id], true)).Changes.IsEmpty);
        Assert.True((await Tasks.AddTagsAsync([task.Id], [tag.Id])).Changes.IsEmpty);
        Assert.True((await Tasks.SetTagsAsync(task.Id, [])).Changes.IsEmpty);
        Assert.True((await Tasks.ReorderAsync(task.Id, 1)).Changes.IsEmpty);
        Assert.True((await Tasks.DuplicateAsync(task.Id, includeSubtasks: true)).Changes.IsEmpty);
        Assert.True((await Tasks.SnoozeAsync(task.Id, Clock.UtcNow)).Changes.IsEmpty);
        Assert.True((await Tasks.SkipOccurrenceAsync(task.Id)).Changes.IsEmpty);
        Assert.True((await Tasks.MarkNotifiedAsync([task.Id], Clock.UtcNow)).Changes.IsEmpty);
        Assert.False((await Tasks.SetParentAsync(task.Id, null, 1)).Succeeded);

        var unchanged = await GetAsync(task.Id);
        Assert.Equal("ゴミ箱のタスク", unchanged.Title);
        Assert.Equal(TaskItemStatus.NotStarted, unchanged.Status);
        Assert.NotNull(unchanged.DeletedAt);
    }

    [Fact]
    public async Task ApplyUndo_AfterPurge_RecreatesTaskWithoutMissingParent()
    {
        var parent = await AddAsync("親");
        var child = await AddAsync("子", parentId: parent.Id, tagNames: ["仕事"]);
        var deletion = await Tasks.SoftDeleteAsync([child.Id]);
        await Tasks.SoftDeleteAsync([parent.Id]);
        await Tasks.PurgeAsync([parent.Id, child.Id]);

        await Tasks.ApplyUndoAsync(deletion.Changes);

        var back = await GetAsync(child.Id);
        Assert.Null(back.ParentTaskId);
        Assert.Equal(0, back.Depth);
        Assert.Single((await Tasks.GetDetailAsync(child.Id))!.TagIds);
    }
}

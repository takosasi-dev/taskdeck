using Microsoft.EntityFrameworkCore;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.Tests.Data;

public sealed class TemplateRepositoryTests : DataTestBase
{
    [Fact]
    public async Task Save_NewTemplate_NormalizesAndOrdersItemsByDepth()
    {
        var parent = Item("荷造りをする", sortOrder: 1024, dueOffsetDays: -1, dueTime: new TimeOnly(20, 0));
        var child = Item("充電器一式", sortOrder: 1024, parentItemId: parent.Id);

        var saved = await Templates.SaveAsync(
            new TaskTemplate { Name = "  出張の準備  ", AnchorLabel = "出発日", Description = "   " },
            [child, parent]);   // 渡す順が前後していても親を先にそろえる

        var loaded = await Templates.GetWithItemsAsync(saved.Id);
        Assert.Equal("出張の準備", loaded!.Template.Name);
        Assert.Equal("出発日", loaded.Template.AnchorLabel);
        Assert.Null(loaded.Template.Description);
        Assert.Equal(SortOrderMath.Step, loaded.Template.SortOrder);
        Assert.Equal(["荷造りをする", "充電器一式"], loaded.Items.Select(i => i.Title));
        Assert.Equal([0, 1], loaded.Items.Select(i => i.Depth));
        Assert.Equal(new TimeOnly(20, 0), loaded.Items[0].DueTime);
        Assert.Equal(-1, loaded.Items[0].DueOffsetDays);
    }

    [Fact]
    public async Task Save_ExistingTemplate_SoftDeletesRemovedItems()
    {
        var keep = Item("残す項目", sortOrder: 1024);
        var drop = Item("消す項目", sortOrder: 2048);
        var saved = await Templates.SaveAsync(new TaskTemplate { Name = "手順" }, [keep, drop]);

        keep.Title = "残す項目（改）";
        await Templates.SaveAsync(saved, [keep]);

        var loaded = await Templates.GetWithItemsAsync(saved.Id);
        Assert.Equal(["残す項目（改）"], loaded!.Items.Select(i => i.Title));
        await using var db = Db.CreateDbContext();
        Assert.NotNull((await db.TemplateItems.SingleAsync(i => i.Id == drop.Id)).DeletedAt);
    }

    [Fact]
    public async Task Save_ItemWithoutTitle_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Templates.SaveAsync(new TaskTemplate { Name = "手順" }, [Item("   ", sortOrder: 1024)]));
    }

    [Fact]
    public async Task GetAll_OrdersByUseCountThenSortOrder_WithItemCount()
    {
        var rare = await Templates.SaveAsync(new TaskTemplate { Name = "あまり使わない", SortOrder = 1024 }, [Item("x", 1024)]);
        var often = await Templates.SaveAsync(new TaskTemplate { Name = "よく使う", SortOrder = 2048 }, [Item("a", 1024), Item("b", 2048)]);

        await Templates.ExpandAsync(Plan(often.Id, [Planned(Guid.CreateVersion7(), null, 0, "a", null)]));

        var all = await Templates.GetAllAsync();
        Assert.Equal(["よく使う", "あまり使わない"], all.Select(s => s.Template.Name));
        Assert.Equal([2, 1], all.Select(s => s.ItemCount));
        Assert.Equal(rare.Id, all[1].Template.Id);
    }

    [Fact]
    public async Task Delete_HidesTemplateAndItems()
    {
        var saved = await Templates.SaveAsync(new TaskTemplate { Name = "使わなくなった" }, [Item("項目", 1024)]);

        await Templates.DeleteAsync(saved.Id);

        Assert.Empty(await Templates.GetAllAsync());
        Assert.Null(await Templates.GetWithItemsAsync(saved.Id));
        await using var db = Db.CreateDbContext();
        Assert.All(await db.TemplateItems.ToListAsync(), i => Assert.NotNull(i.DeletedAt));
    }

    [Fact]
    public async Task Expand_AddsTasksAtEnd_AndCountsUse()
    {
        var template = await Templates.SaveAsync(new TaskTemplate { Name = "出張の準備" }, [Item("荷造り", 1024)]);
        var existing = await AddAsync("先にあるタスク");
        var rootId = Guid.CreateVersion7();
        var childId = Guid.CreateVersion7();
        var batchId = Guid.CreateVersion7();
        var plan = new ExpansionPlan(
            template.Id,
            batchId,
            new DateOnly(2026, 9, 25),
            [
                Planned(rootId, null, 0, "荷造りをする", Day(9, 25), remindOffsetMinutes: 60),
                Planned(childId, rootId, 1, "充電器一式", null),
            ],
            0);

        var result = await Templates.ExpandAsync(plan);

        Assert.Equal(2, result.Created.Count);
        var root = await GetAsync(rootId);
        Assert.Equal(batchId, root.TemplateBatchId);
        Assert.Equal(Day(9, 25), root.DueAt);
        Assert.Equal(At(9, 25, 8), root.RemindAt);           // 既定リマインド 9:00 の1時間前
        Assert.True(root.SortOrder > existing.SortOrder);    // 一覧の末尾に足す
        var child = await GetAsync(childId);
        Assert.Equal(rootId, child.ParentTaskId);
        Assert.Equal(1, child.Depth);

        var summary = Assert.Single(await Templates.GetAllAsync());
        Assert.Equal(1, summary.Template.UseCount);
        Assert.Equal(Clock.UtcNow, summary.Template.LastUsedAt);

        await Tasks.ApplyUndoAsync(result.Changes);
        Assert.Null(await Tasks.GetAsync(rootId));
        Assert.Null(await Tasks.GetAsync(childId));
    }

    [Fact]
    public async Task CreateFromTasks_UsesRelativeDays_AndKeepsHierarchy()
    {
        var root = await AddAsync("会議の準備", At(9, 25, 14), dueHasTime: true, tagNames: ["仕事"], projectName: "業務改善");
        var child = await AddAsync("資料を印刷する", Day(9, 24), parentId: root.Id);
        await AddAsync("部数を数える", parentId: child.Id);

        // 子も一緒に選んでも、親の下に1回だけ入る
        var result = await Templates.CreateFromTasksAsync("会議の準備", [root.Id, child.Id], new DateOnly(2026, 9, 25));

        Assert.True(result.Succeeded);
        var loaded = await Templates.GetWithItemsAsync(result.Value!.Id);
        Assert.Equal(3, loaded!.Items.Count);
        var rootItem = loaded.Items[0];
        Assert.Equal(0, rootItem.DueOffsetDays);
        Assert.Equal(new TimeOnly(14, 0), rootItem.DueTime);
        Assert.Single(rootItem.TagIds);
        var childItem = loaded.Items.Single(i => i.Title == "資料を印刷する");
        Assert.Equal(-1, childItem.DueOffsetDays);
        Assert.Null(childItem.DueTime);
        Assert.Equal(rootItem.Id, childItem.ParentItemId);
        var grandchildItem = loaded.Items.Single(i => i.Title == "部数を数える");
        Assert.Equal(2, grandchildItem.Depth);
        Assert.Null(grandchildItem.DueOffsetDays);
        Assert.NotNull(result.Value.DefaultProjectId);
    }

    [Fact]
    public async Task CreateFromTasks_NoLiveTask_Fails()
    {
        var task = await AddAsync("消したタスク");
        await Tasks.SoftDeleteAsync([task.Id]);

        var result = await Templates.CreateFromTasksAsync("テンプレート", [task.Id], new DateOnly(2026, 9, 25));

        Assert.False(result.Succeeded);
        Assert.Equal("テンプレートにするタスクがありません。", result.Error);
    }

    [Fact]
    public async Task SeedDefaults_FirstCallOnly_InsertsThreeTemplates()
    {
        Assert.True(await Templates.SeedDefaultsAsync());

        var all = await Templates.GetAllAsync();
        Assert.Equal(["週次レビュー", "買い物リスト", "新しい課題に着手"], all.Select(s => s.Template.Name));
        Assert.Equal([5, 6, 4], all.Select(s => s.ItemCount));

        var weekly = await Templates.GetWithItemsAsync(all[0].Template.Id);
        Assert.Equal("対象の週", weekly!.Template.AnchorLabel);
        Assert.All(weekly.Items, i => Assert.Equal(0, i.DueOffsetDays));

        var shopping = await Templates.GetWithItemsAsync(all[1].Template.Id);
        Assert.Equal("買い物の日", shopping!.Template.AnchorLabel);
        Assert.All(shopping.Items, i => Assert.Null(i.DueOffsetDays));
        Assert.Equal(4, shopping.Items.Count(i => i.Depth == 1));

        var issue = await Templates.GetWithItemsAsync(all[2].Template.Id);
        Assert.Equal("着手日", issue!.Template.AnchorLabel);
        Assert.Equal([-1, 0, 3, 7], issue.Items.Select(i => i.DueOffsetDays));

        Assert.False(await Templates.SeedDefaultsAsync());
        Assert.Equal(3, (await Templates.GetAllAsync()).Count);
    }

    private static TaskTemplateItem Item(
        string title,
        double sortOrder = 1024,
        Guid? parentItemId = null,
        int? dueOffsetDays = null,
        TimeOnly? dueTime = null) => new()
    {
        Title = title,
        SortOrder = sortOrder,
        ParentItemId = parentItemId,
        Depth = parentItemId is null ? 0 : 1,
        DueOffsetDays = dueOffsetDays,
        DueTime = dueTime,
    };

    private static ExpansionPlan Plan(Guid templateId, IReadOnlyList<PlannedTask> tasks) =>
        new(templateId, Guid.CreateVersion7(), new DateOnly(2026, 9, 22), tasks, 0);

    private static PlannedTask Planned(
        Guid newId,
        Guid? parentNewId,
        int depth,
        string title,
        DateTime? dueAt,
        int? remindOffsetMinutes = null) =>
        new(newId, Guid.CreateVersion7(), parentNewId, depth, title, null, Priority.None, null, null, null, null,
            dueAt, false, false, remindOffsetMinutes, null, null, [], (depth + 1) * SortOrderMath.Step);
}

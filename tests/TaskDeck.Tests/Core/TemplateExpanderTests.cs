using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Core;

/// <summary>設計書 4.5 の境界条件表。時計は JST 2026-09-22（火）10:00。</summary>
public class TemplateExpanderTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static TaskTemplate Template(Guid? project = null, params Guid[] tags) =>
        new() { Name = "出張の準備", DefaultProjectId = project, DefaultTagIds = [.. tags] };

    private static TaskTemplateItem Item(
        string title,
        int? offset = null,
        TimeOnly? time = null,
        Guid? id = null,
        Guid? parent = null,
        int depth = 0,
        double sortOrder = 0,
        Guid[]? tags = null,
        int? remind = null,
        int? duration = null,
        Priority priority = Priority.None) =>
        new()
        {
            Id = id ?? Guid.CreateVersion7(),
            Title = title,
            DueOffsetDays = offset,
            DueTime = time,
            ParentItemId = parent,
            Depth = depth,
            SortOrder = sortOrder,
            TagIds = [.. tags ?? []],
            RemindOffsetMinutes = remind,
            DurationMinutes = duration,
            Priority = priority,
        };

    private static ExpansionOptions Options(
        DateOnly? anchor = null,
        bool pullPast = true,
        Guid[]? excluded = null,
        Guid? project = null,
        IReadOnlyList<Guid>? tags = null) =>
        new()
        {
            AnchorDate = anchor ?? Today,
            PullPastToToday = pullPast,
            ExcludedItemIds = new HashSet<Guid>(excluded ?? []),
            ProjectOverride = project,
            TagOverride = tags,
        };

    private static ExpansionPlan Plan(IReadOnlyList<TaskTemplateItem> items, ExpansionOptions options, TaskTemplate? template = null) =>
        new TemplateExpander(Clock).Plan(template ?? Template(), items, options);

    [Fact]
    public void Plan_基準日から相対日数で期限を決める()
    {
        var plan = Plan([Item("航空券", -7), Item("印刷", 0, sortOrder: 1), Item("精算", 3, sortOrder: 2)], Options(anchor: new DateOnly(2026, 9, 25)));

        Assert.Equal([new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 28)], plan.Tasks.Select(t => t.OriginalDate));
        Assert.Equal(1, plan.PastCount);
        Assert.Equal([true, false, false], plan.Tasks.Select(t => t.IsPast));
    }

    [Fact]
    public void Plan_今日に寄せるON_過去のぶんだけ今日になる()
    {
        var items = new[] { Item("7日前", -7), Item("3日前", -3, sortOrder: 1), Item("3日後", 3, sortOrder: 2) };

        var plan = Plan(items, Options(pullPast: true));

        Assert.Equal([Today, Today, Today.AddDays(3)], plan.Tasks.Select(t => t.ResolvedDate));
        Assert.Equal([Today.AddDays(-7), Today.AddDays(-3), Today.AddDays(3)], plan.Tasks.Select(t => t.OriginalDate));
        Assert.Equal(2, plan.PastCount);
    }

    [Fact]
    public void Plan_今日に寄せるOFF_過去の期限のまま作る()
    {
        var plan = Plan([Item("7日前", -7)], Options(pullPast: false));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal(Today.AddDays(-7), task.ResolvedDate);
        Assert.Equal(Clock.LocalDayStartUtc(Today.AddDays(-7)), task.DueAt);
        Assert.True(task.IsPast);
    }

    [Fact]
    public void Plan_オフセットなし_期限なしで過去に数えない()
    {
        var plan = Plan([Item("買うもの")], Options());

        var task = Assert.Single(plan.Tasks);
        Assert.Null(task.DueAt);
        Assert.Null(task.OriginalDate);
        Assert.False(task.DueHasTime);
        Assert.False(task.IsPast);
        Assert.Equal(0, plan.PastCount);
    }

    [Fact]
    public void Plan_時刻つきの前日_前日のその時刻()
    {
        var plan = Plan([Item("荷造り", -1, new TimeOnly(20, 0))], Options(anchor: new DateOnly(2026, 9, 25)));

        var task = Assert.Single(plan.Tasks);
        Assert.True(task.DueHasTime);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 24, 20, 0), task.DueAt);
    }

    [Fact]
    public void Plan_月をまたぐオフセット()
    {
        var plan = Plan([Item("報告", 10)], Options(anchor: new DateOnly(2026, 9, 25)));

        Assert.Equal(new DateOnly(2026, 10, 5), Assert.Single(plan.Tasks).OriginalDate);
    }

    [Fact]
    public void Plan_親を外すと子孫も作らない()
    {
        var parent = Guid.CreateVersion7();
        var child = Guid.CreateVersion7();
        var items = new[]
        {
            Item("荷造り", id: parent),
            Item("充電器", id: child, parent: parent, depth: 1),
            Item("ケーブル", parent: child, depth: 2),
            Item("精算", 3, sortOrder: 1),
        };

        var plan = Plan(items, Options(excluded: [parent]));

        Assert.Equal(["精算"], plan.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void Plan_子だけ外すと親は作られる()
    {
        var parent = Guid.CreateVersion7();
        var child = Guid.CreateVersion7();
        var items = new[] { Item("荷造り", id: parent), Item("充電器", id: child, parent: parent, depth: 1) };

        var plan = Plan(items, Options(excluded: [child]));

        Assert.Equal(["荷造り"], plan.Tasks.Select(t => t.Title));
    }

    [Fact]
    public void Plan_3階層_親子を写して深さを数え直す()
    {
        var parent = Guid.CreateVersion7();
        var child = Guid.CreateVersion7();
        var items = new[]
        {
            Item("孫", parent: child, depth: 2, sortOrder: 0),
            Item("子", id: child, parent: parent, depth: 1, sortOrder: 0),
            Item("親", id: parent, depth: 0, sortOrder: 0),
        };

        var plan = Plan(items, Options());

        Assert.Equal(["親", "子", "孫"], plan.Tasks.Select(t => t.Title));
        Assert.Equal([0, 1, 2], plan.Tasks.Select(t => t.Depth));
        var byTitle = plan.Tasks.ToDictionary(t => t.Title);
        Assert.Null(byTitle["親"].ParentNewId);
        Assert.Equal(byTitle["親"].NewId, byTitle["子"].ParentNewId);
        Assert.Equal(byTitle["子"].NewId, byTitle["孫"].ParentNewId);
    }

    [Fact]
    public void Plan_プロジェクトは指定_テンプレート既定の順で決まる()
    {
        var templateProject = Guid.CreateVersion7();
        var chosen = Guid.CreateVersion7();

        Assert.Equal(templateProject, Assert.Single(Plan([Item("A")], Options(), Template(templateProject)).Tasks).ProjectId);
        Assert.Equal(chosen, Assert.Single(Plan([Item("A")], Options(project: chosen), Template(templateProject)).Tasks).ProjectId);
        Assert.Null(Assert.Single(Plan([Item("A")], Options()).Tasks).ProjectId);
    }

    [Fact]
    public void Plan_タグは指定があれば上書き_なければテンプレート既定と項目を合わせる()
    {
        Guid templateTag = Guid.CreateVersion7(), itemTag = Guid.CreateVersion7(), chosen = Guid.CreateVersion7();
        var template = Template(null, templateTag);

        var merged = Assert.Single(Plan([Item("A", tags: [itemTag, templateTag])], Options(), template).Tasks);
        Assert.Equal([templateTag, itemTag], merged.TagIds);

        var overridden = Assert.Single(Plan([Item("A", tags: [itemTag])], Options(tags: [chosen]), template).Tasks);
        Assert.Equal([chosen], overridden.TagIds);
    }

    [Fact]
    public void Plan_通知と所要時間と並び順を写す()
    {
        var plan = Plan([Item("A", 0, remind: 30, duration: 45, sortOrder: 2048, priority: Priority.High)], Options());

        var task = Assert.Single(plan.Tasks);
        Assert.Equal(30, task.RemindOffsetMinutes);
        Assert.Equal(45, task.DurationMinutes);
        Assert.Equal(2048, task.SortOrder);
        Assert.Equal(Priority.High, task.Priority);
    }

    [Fact]
    public void ToRequests_全件に同じBatchIdと新しいIdを付ける()
    {
        var plan = Plan([Item("A", 0), Item("B", 1, sortOrder: 1)], Options());

        var requests = plan.ToRequests();

        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal(plan.BatchId, r.TemplateBatchId));
        Assert.Equal([.. plan.Tasks.Select(t => t.NewId)], requests.Select(r => r.Id));
    }

    [Fact]
    public void Plan_項目が空_空の計画()
    {
        var plan = Plan([], Options());

        Assert.Empty(plan.Tasks);
        Assert.Equal(0, plan.PastCount);
        Assert.Equal(Today, plan.AnchorDate);
    }

    [Theory]
    [InlineData(-7, "7日前")]
    [InlineData(-1, "前日")]
    [InlineData(0, "当日")]
    [InlineData(1, "翌日")]
    [InlineData(3, "3日後")]
    public void DescribeOffset_相対日付の表記(int offset, string expected) =>
        Assert.Equal(expected, TemplateExpander.DescribeOffset(offset));
}

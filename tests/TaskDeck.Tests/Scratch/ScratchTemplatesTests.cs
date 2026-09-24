using TaskDeck.Core.Entities;
using TaskDeck.Core.Scratch;
using TaskDeck.Core.Services;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Scratch;

/// <summary>テンプレートの項目 ⇔ 使い捨てリストの項目（題名と階層だけ）。</summary>
public class ScratchTemplatesTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private static TaskTemplateItem Item(string title, TaskTemplateItem? parent = null, double sortOrder = 1024, int? depth = null) => new()
    {
        Title = title,
        ParentItemId = parent?.Id,
        Depth = depth ?? (parent is null ? 0 : parent.Depth + 1),
        SortOrder = sortOrder,
    };

    private static string Shape(IEnumerable<ScratchItem> items) => string.Join(' ', items.Select(i => i.Text + i.Depth));

    [Fact]
    public void FromTemplate_親の直後に子_兄弟はSortOrder順()
    {
        var food = Item("食料品", sortOrder: 1024);
        var daily = Item("日用品", sortOrder: 2048);
        // リポジトリは Depth → SortOrder の順で返すので、木の順とは限らない
        var items = new[]
        {
            food,
            daily,
            Item("パン", food, 3072),
            Item("牛乳", food, 1024),
            Item("ティッシュ", daily, 1024),
            Item("卵", food, 2048),
        };

        var result = ScratchTemplates.FromTemplate(items);

        Assert.Equal("食料品0 牛乳1 卵1 パン1 日用品0 ティッシュ1", Shape(result));
        Assert.All(result, i => Assert.False(i.IsChecked));
    }

    [Fact]
    public void FromTemplate_親が無い項目_いちばん上の段に置く()
    {
        var orphan = Item("迷子", sortOrder: 2048, depth: 1);
        orphan.ParentItemId = Guid.CreateVersion7();

        var result = ScratchTemplates.FromTemplate([Item("先頭", sortOrder: 1024), orphan]);

        Assert.Equal("先頭0 迷子0", Shape(result));
    }

    [Fact]
    public void FromTemplate_3段より深い項目_消さずに3段目に並べる()
    {
        var a = Item("A");
        var b = Item("B", a);
        var c = Item("C", b);
        var d = Item("D", c);

        var result = ScratchTemplates.FromTemplate([a, b, c, d]);

        Assert.Equal("A0 B1 C2 D2", Shape(result));
    }

    [Fact]
    public void ToTemplateItems_深さから親を組み立てる_空の行は入れない()
    {
        var items = new List<ScratchItem>
        {
            new() { Text = "食料品", Depth = 0 },
            new() { Text = "牛乳", Depth = 1, IsChecked = true },
            new() { Text = "  ", Depth = 1 },
            new() { Text = "パン", Depth = 1 },
            new() { Text = "食パン 6枚切り", Depth = 2 },
            new() { Text = "日用品", Depth = 0 },
        };

        var result = ScratchTemplates.ToTemplateItems(items);

        Assert.Equal(["食料品", "牛乳", "パン", "食パン 6枚切り", "日用品"], result.Select(i => i.Title));
        Assert.Equal([0, 1, 1, 2, 0], result.Select(i => i.Depth));
        Assert.Equal([null, result[0].Id, result[0].Id, result[2].Id, null], result.Select(i => i.ParentItemId));
        Assert.True(result.Select(i => i.SortOrder).SequenceEqual(result.Select(i => i.SortOrder).Order()));
    }

    [Fact]
    public void ToTemplateItems_空の親を飛ばした子_1段浅くして前の行につなぐ()
    {
        var items = new List<ScratchItem>
        {
            new() { Text = "", Depth = 0 },
            new() { Text = "子", Depth = 1 },
            new() { Text = "孫", Depth = 2 },
        };

        var result = ScratchTemplates.ToTemplateItems(items);

        Assert.Equal([0, 1], result.Select(i => i.Depth));
        Assert.Equal([null, result[0].Id], result.Select(i => i.ParentItemId));
    }

    [Fact]
    public async Task ToTemplateItems_保存して読み直して戻す_同じ題名と階層になる()
    {
        using var db = new TestDatabase(Clock);
        var repository = new TemplateRepository(db, Clock, new FakeSettingsStore(), new DataChangeHub());
        var items = new List<ScratchItem>
        {
            new() { Text = "食料品", Depth = 0 },
            new() { Text = "牛乳", Depth = 1 },
            new() { Text = "パン", Depth = 1 },
            new() { Text = "食パン 6枚切り", Depth = 2 },
            new() { Text = "日用品", Depth = 0 },
        };

        var saved = await repository.SaveAsync(new TaskTemplate { Name = "買い物" }, ScratchTemplates.ToTemplateItems(items));
        var loaded = await repository.GetWithItemsAsync(saved.Id);

        Assert.NotNull(loaded);
        Assert.Equal(Shape(items), Shape(ScratchTemplates.FromTemplate(loaded.Items)));
    }
}

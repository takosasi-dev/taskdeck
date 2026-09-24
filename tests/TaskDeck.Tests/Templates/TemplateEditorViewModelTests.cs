using TaskDeck.App.Views.Templates;
using TaskDeck.Core;
using TaskDeck.Core.Entities;

namespace TaskDeck.Tests.Templates;

/// <summary>テンプレートの新規作成・編集（項目はアウトライナーと同じく「並び＋深さ」で持つ）。</summary>
public class TemplateEditorViewModelTests
{
    private static TemplateEditorViewModel Editor(TaskTemplate? source = null, params TaskTemplateItem[] items) =>
        new(source, items, [], new Dictionary<Guid, Project>());

    /// <summary>タイトルと深さを並びのとおりに入れた編集画面（新規）。</summary>
    private static TemplateEditorViewModel Outline(params (string Title, int Level)[] rows)
    {
        var editor = Editor();
        editor.Name = "手順";
        editor.Items.Clear();
        foreach (var (title, level) in rows)
        {
            editor.Items.Add(new EditorItem { Title = title, Level = level });
        }
        editor.SelectedItem = editor.Items[0];
        return editor;
    }

    private static EditorItem At(TemplateEditorViewModel editor, string title) => editor.Items.Single(i => i.Title == title);

    private static string Shape(TemplateEditorViewModel editor) =>
        string.Join(" ", editor.Items.Select(i => new string('-', i.Level) + i.Title));

    [Fact]
    public void Constructor_既存の項目_親の直後に子が来る木の順に並ぶ()
    {
        var template = new TaskTemplate { Name = "出張の準備" };
        var packing = new TaskTemplateItem { Title = "荷造り", SortOrder = 1024 };
        var ticket = new TaskTemplateItem { Title = "航空券", SortOrder = 512 };
        var charger = new TaskTemplateItem { Title = "充電器", ParentItemId = packing.Id, Depth = 1, SortOrder = 1024 };
        var cable = new TaskTemplateItem { Title = "ケーブル", ParentItemId = charger.Id, Depth = 2, SortOrder = 1024 };

        var editor = Editor(template, packing, ticket, charger, cable);   // Depth 順で渡ってくる

        Assert.Equal("航空券 荷造り -充電器 --ケーブル", Shape(editor));
        Assert.Equal("出張の準備", editor.Name);
        Assert.False(editor.IsNew);
        Assert.Equal("テンプレートを編集", editor.Heading);
    }

    [Fact]
    public void AddItem_子孫を持つ項目で押す_子孫の後ろに同じ深さで足して選ぶ()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("A2", 1), ("B", 0));
        editor.SelectedItem = At(editor, "A");

        editor.AddItemCommand.Execute(null);

        Assert.Equal("A -A1 -A2  B", Shape(editor));
        Assert.Equal(0, editor.SelectedItem!.Level);
        Assert.Equal(3, editor.Items.IndexOf(editor.SelectedItem));
    }

    [Fact]
    public void AddChild_押す_最後の子として足す()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("B", 0));
        editor.SelectedItem = At(editor, "A");

        editor.AddChildCommand.Execute(null);

        Assert.Equal("A -A1 - B", Shape(editor));
        Assert.Equal(1, editor.SelectedItem!.Level);
    }

    [Fact]
    public void AddChild_3階層目の項目_押せない()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("A11", 2));

        editor.SelectedItem = At(editor, "A11");

        Assert.False(editor.AddChildCommand.CanExecute(null));
    }

    [Fact]
    public void Indent_直前に兄弟がある_その最後の子になり子孫も下がる()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("B", 0), ("B1", 1));
        editor.SelectedItem = At(editor, "B");

        editor.IndentCommand.Execute(null);

        Assert.Equal("A -A1 -B --B1", Shape(editor));
    }

    [Fact]
    public void Indent_先頭の項目や3階層を超える項目_下げられない()
    {
        var editor = Outline(("A", 0), ("B", 0), ("B1", 1), ("B11", 2));

        editor.SelectedItem = At(editor, "A");
        Assert.False(editor.IndentCommand.CanExecute(null));
        editor.SelectedItem = At(editor, "B");
        Assert.False(editor.IndentCommand.CanExecute(null));   // B11 が4階層目になる
    }

    [Fact]
    public void Outdent_子を上げる_親の子孫の後ろへ出てほかの子は親に残る()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("A11", 2), ("A2", 1), ("B", 0));
        editor.SelectedItem = At(editor, "A1");

        editor.OutdentCommand.Execute(null);

        Assert.Equal("A -A2 A1 -A11 B", Shape(editor));
        Assert.Same(At(editor, "A1"), editor.SelectedItem);
        Assert.False(editor.OutdentCommand.CanExecute(null));
    }

    [Fact]
    public void MoveUp_兄弟の前へ_子孫ごと入れ替わる()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("B", 0), ("B1", 1));
        editor.SelectedItem = At(editor, "B");

        editor.MoveUpCommand.Execute(null);

        Assert.Equal("B -B1 A -A1", Shape(editor));
        Assert.Same(At(editor, "B"), editor.SelectedItem);
        Assert.False(editor.MoveUpCommand.CanExecute(null));
        Assert.True(editor.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void MoveDown_次の兄弟の後ろへ_子孫ごと入れ替わり親をまたがない()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("A2", 1), ("B", 0));
        editor.SelectedItem = At(editor, "A1");

        editor.MoveDownCommand.Execute(null);

        Assert.Equal("A -A2 -A1 B", Shape(editor));
        Assert.False(editor.MoveDownCommand.CanExecute(null));   // 最後の子は親の外へ出ない
    }

    [Fact]
    public void Remove_子孫ごと消して次の項目を選ぶ()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("B", 0));

        editor.RemoveCommand.Execute(null);

        Assert.Equal("B", Shape(editor));
        Assert.Same(At(editor, "B"), editor.SelectedItem);
    }

    [Fact]
    public void TryBuild_並びと深さ_親子と兄弟ごとの並び順に直す()
    {
        var editor = Outline(("A", 0), ("A1", 1), ("A11", 2), ("A2", 1), ("B", 0));

        Assert.True(editor.TryBuild(out var template, out var items));

        Assert.Equal("手順", template.Name);
        Assert.Equal(["A", "A1", "A11", "A2", "B"], items.Select(i => i.Title));
        Assert.Equal([0, 1, 2, 1, 0], items.Select(i => i.Depth));
        var a = items[0];
        Assert.Null(a.ParentItemId);
        Assert.Equal(a.Id, items[1].ParentItemId);
        Assert.Equal(items[1].Id, items[2].ParentItemId);
        Assert.Equal(a.Id, items[3].ParentItemId);
        Assert.Equal([1024d, 1024d, 1024d, 2048d, 2048d], items.Select(i => i.SortOrder));
        Assert.Null(editor.Error);
    }

    [Fact]
    public void TryBuild_日数と時刻_基準日からの日数と時刻として入る()
    {
        var editor = Outline(("荷造り", 0));
        var item = editor.Items[0];
        item.OffsetText = "－１";          // 全角でも読む
        item.TimeText = "２０：００";
        item.Priority = Priority.High;
        item.Notes = "  ";

        Assert.True(editor.TryBuild(out _, out var items));

        Assert.Equal(-1, items[0].DueOffsetDays);
        Assert.Equal(new TimeOnly(20, 0), items[0].DueTime);
        Assert.Equal(Priority.High, items[0].Priority);
        Assert.Null(items[0].Notes);
        Assert.Equal("前日 20:00", item.DueSummary);
    }

    [Fact]
    public void TryBuild_名前が空_保存せず理由を出す()
    {
        var editor = Outline(("A", 0));
        editor.Name = "   ";

        Assert.False(editor.TryBuild(out _, out _));

        Assert.Equal("名前を入れてください", editor.Error);
    }

    [Fact]
    public void TryBuild_日数が読めない_理由を出してその項目を選ぶ()
    {
        var editor = Outline(("A", 0), ("B", 0));
        At(editor, "B").OffsetText = "三日前";

        Assert.False(editor.TryBuild(out _, out _));

        Assert.Equal("「B」の日数が読めません（例: -3 で3日前）", editor.Error);
        Assert.Same(At(editor, "B"), editor.SelectedItem);
        Assert.True(At(editor, "B").HasOffsetError);
    }

    [Fact]
    public void TryBuild_時刻が読めない_保存しない()
    {
        var editor = Outline(("A", 0));
        editor.Items[0].TimeText = "25:00";

        Assert.False(editor.TryBuild(out _, out _));

        Assert.Equal("「A」の時刻が読めません（例: 20:00）", editor.Error);
    }

    [Fact]
    public void TryBuild_打ちかけの空の行_子がなければ捨てる()
    {
        var editor = Outline(("A", 0), ("", 0), ("B", 0), (" ", 1));

        Assert.True(editor.TryBuild(out _, out var items));

        Assert.Equal(["A", "B"], items.Select(i => i.Title));
    }

    [Fact]
    public void TryBuild_空のタイトルに子がある_保存しない()
    {
        var editor = Outline(("", 0), ("子", 1));

        Assert.False(editor.TryBuild(out _, out _));

        Assert.Equal("タイトルが空の項目にサブ項目があります", editor.Error);
    }

    [Fact]
    public void TryBuild_項目がない_保存しない()
    {
        var editor = Outline(("", 0));

        Assert.False(editor.TryBuild(out _, out _));

        Assert.Equal("項目を1つ以上入れてください", editor.Error);
    }

    [Fact]
    public void TryBuild_既存のテンプレート_Idと並び順と使用履歴を保ち画面に出さない値も消さない()
    {
        var template = new TaskTemplate { Name = "出張の準備", SortOrder = 5000, UseCount = 3, LastUsedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), AnchorLabel = "出発日" };
        var item = new TaskTemplateItem { Title = "荷造り", DueOffsetDays = -1, RemindOffsetMinutes = 30, DurationMinutes = 60 };
        var editor = Editor(template, item);

        Assert.True(editor.TryBuild(out var built, out var items));

        Assert.Equal(template.Id, built.Id);
        Assert.Equal(5000, built.SortOrder);
        Assert.Equal(3, built.UseCount);
        Assert.Equal("出発日", built.AnchorLabel);
        Assert.Equal(item.Id, items[0].Id);
        Assert.Equal(30, items[0].RemindOffsetMinutes);
        Assert.Equal(60, items[0].DurationMinutes);
    }

    [Fact]
    public void ItemTags_選んだ項目のタグを選び直す_その項目に入る()
    {
        var work = new Tag { Name = "仕事" };
        var editor = new TemplateEditorViewModel(null, [], [work], new Dictionary<Guid, Project>());
        editor.Name = "手順";
        editor.Items[0].Title = "A";

        editor.ItemTags.Choices.Single().IsSelected = true;

        Assert.Equal([work.Id], editor.Items[0].TagIds);
        Assert.Equal("#仕事", editor.ItemTags.Summary);
        Assert.True(editor.TryBuild(out _, out var items));
        Assert.Equal([work.Id], items[0].TagIds);
    }

    [Theory]
    [InlineData("", "期限なし")]
    [InlineData("0", "当日")]
    [InlineData("-7", "7日前")]
    [InlineData("+3", "3日後")]
    [InlineData("x", "日数が読めません（例: -3 で3日前）")]
    public void EditorItem_日数の欄_相対の言い方で添える(string text, string expected)
    {
        Assert.Equal(expected, new EditorItem { OffsetText = text }.OffsetHint);
    }
}

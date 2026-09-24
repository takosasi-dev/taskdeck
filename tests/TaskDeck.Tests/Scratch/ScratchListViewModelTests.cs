using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views.Scratch;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Scratch;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Scratch;

/// <summary>使い捨てリストの中身（キー操作の中身・全部チェックで「捨てる」が主ボタン・捨てる前の確認・テンプレートとして保存）。</summary>
public class ScratchListViewModelTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private readonly ScratchStore _store = new(new MemoryAppState(), Clock);
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private int _changes;

    private ScratchListViewModel Open(params (string Text, int Depth, bool Checked)[] items)
    {
        var list = _store.Create("買い物", [.. items.Select(i => new ScratchItem { Text = i.Text, Depth = i.Depth, IsChecked = i.Checked })]);
        return new ScratchListViewModel(list, _store, _templates, () => _changes++, NullLogger.Instance);
    }

    private static string Shape(ScratchListViewModel vm) => string.Join(' ', vm.Items.Select(i => i.Text + i.Depth));

    [Fact]
    public void AddFromBoxCommand_打った文字_末尾のいちばん上の段に足して欄を空にする()
    {
        var vm = Open(("牛乳", 0, false), ("低脂肪", 1, false));

        vm.AddText = "  卵 ";
        vm.AddFromBoxCommand.Execute(null);

        Assert.Equal("牛乳0 低脂肪1 卵0", Shape(vm));
        Assert.Equal("", vm.AddText);
        Assert.Equal(["牛乳", "低脂肪", "卵"], _store.Lists[0].Items.Select(i => i.Text));
        Assert.Equal(1, _changes);
        Assert.Equal("残り 3 件（全 3 件）", vm.Summary);
    }

    [Fact]
    public void AddFromBoxCommand_空白だけ_押せない()
    {
        var vm = Open();

        vm.AddText = "   ";

        Assert.False(vm.AddFromBoxCommand.CanExecute(null));
        Assert.False(vm.HasAddText);
    }

    [Fact]
    public void InsertBelow_Enter_すぐ下に空の行を足してモデルにも入れる()
    {
        var vm = Open(("パン", 0, false), ("食パン", 1, false), ("卵", 0, false));

        var added = vm.InsertBelow(vm.Items[0]);

        Assert.Same(added, vm.Items[1]);
        Assert.Equal("パン0 1 食パン1 卵0", Shape(vm));
        Assert.Same(added.Model, _store.Lists[0].Items[1]);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void Indent_Tab_子ごと下げて字下げの幅も変わる()
    {
        var vm = Open(("食料品", 0, false), ("パン", 0, false), ("食パン", 1, false));
        var changed = new List<string?>();
        vm.Items[2].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(vm.Indent(vm.Items[1]));

        Assert.Equal("食料品0 パン1 食パン2", Shape(vm));
        Assert.Equal(2 * ScratchItemViewModel.IndentStep, vm.Items[2].Indent);
        Assert.Contains(nameof(ScratchItemViewModel.Indent), changed);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void Indent_前の行より2段深くなる_何もしない()
    {
        var vm = Open(("食料品", 0, false), ("パン", 1, false));

        Assert.False(vm.Indent(vm.Items[1]));
        Assert.False(vm.Indent(vm.Items[0]));

        Assert.Equal("食料品0 パン1", Shape(vm));
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void Outdent_ShiftTab_子ごと上げる()
    {
        var vm = Open(("食料品", 0, false), ("パン", 1, false), ("食パン", 2, false));

        Assert.True(vm.Outdent(vm.Items[1]));

        Assert.Equal("食料品0 パン0 食パン1", Shape(vm));
    }

    [Fact]
    public void Remove_空の行でBackspace_消して前の行を返し子は1段上がる()
    {
        var vm = Open(("食料品", 0, false), ("", 1, false), ("牛乳", 2, false));

        var focus = vm.Remove(vm.Items[1]);

        Assert.Same(vm.Items[0], focus);
        Assert.Equal("食料品0 牛乳1", Shape(vm));
        Assert.Equal(1, vm.Items[1].Model.Depth);
    }

    [Fact]
    public void Remove_先頭と最後の1行_次の行か入力欄へ()
    {
        var vm = Open(("", 0, false), ("卵", 0, false));

        var next = vm.Remove(vm.Items[0]);
        var none = vm.Remove(vm.Items[0]);

        Assert.Equal("卵", next?.Text);
        Assert.Null(none);
        Assert.Empty(_store.Lists[0].Items);
    }

    [Fact]
    public void Toggle_CtrlEnter_チェックを切り替えて残りの件数が変わる()
    {
        var vm = Open(("牛乳", 0, false), ("卵", 0, false));

        vm.Toggle(vm.Items[0]);

        Assert.True(vm.Items[0].IsChecked);
        Assert.True(_store.Lists[0].Items[0].IsChecked);
        Assert.Equal(1, vm.RemainingCount);
        Assert.False(vm.IsAllChecked);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void IsAllChecked_文字のある項目を全部チェック_空の行は数えずに主ボタンにする()
    {
        var vm = Open(("牛乳", 0, true), ("", 0, false), ("卵", 1, false));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Items[2].IsChecked = true;

        Assert.True(vm.IsAllChecked);
        Assert.Equal("2 件すべてチェックしました", vm.Summary);
        Assert.Contains(nameof(ScratchListViewModel.IsAllChecked), raised);
    }

    [Fact]
    public void IsAllChecked_項目が無い_主ボタンにしない()
    {
        var vm = Open(("", 0, false));

        Assert.False(vm.IsAllChecked);
        Assert.Equal("項目はまだありません", vm.Summary);
    }

    [Fact]
    public void DiscardCommand_全部チェック済み_確認せずに捨てる()
    {
        var vm = Open(("牛乳", 0, true));
        var asked = 0;
        vm.ConfirmDiscard = _ => { asked++; return false; };

        vm.DiscardCommand.Execute(null);

        Assert.Equal(0, asked);
        Assert.Empty(_store.Lists);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void DiscardCommand_未チェックが残る_1回確認してやめたら残す()
    {
        var vm = Open(("牛乳", 0, true), ("卵", 0, false), ("パン", 0, false));
        ConfirmRequest? request = null;
        vm.ConfirmDiscard = r => { request = r; return false; };

        vm.DiscardCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Contains("2 件", request.Message, StringComparison.Ordinal);
        Assert.Single(_store.Lists);
    }

    [Fact]
    public void DiscardCommand_未チェックが残る_確認でOKなら捨てる()
    {
        var vm = Open(("卵", 0, false));
        vm.ConfirmDiscard = _ => true;

        vm.DiscardCommand.Execute(null);

        Assert.Empty(_store.Lists);
    }

    [Fact]
    public void DiscardCommand_確認を出せる画面が無い_捨てない()
    {
        var vm = Open(("卵", 0, false));

        vm.DiscardCommand.Execute(null);

        Assert.Single(_store.Lists);
    }

    [Fact]
    public void NameText_打つたびに名前を変え_空にして離れたら元の名前に戻す()
    {
        var vm = Open();

        vm.NameText = "週末の買い物";
        Assert.Equal("週末の買い物", _store.Lists[0].Name);

        vm.NameText = "  ";
        Assert.Equal("週末の買い物", _store.Lists[0].Name);
        vm.CommitName();

        Assert.Equal("週末の買い物", vm.NameText);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task SaveAsTemplateCommand_今の題名と階層_そのまま新しいテンプレートにして知らせる()
    {
        var vm = Open(("パン", 0, true), ("食パン 6枚切り", 1, false), ("", 0, false), ("卵", 0, false));
        IReadOnlyList<TaskTemplateItem>? savedItems = null;
        _templates.SaveAsync(Arg.Any<TaskTemplate>(), Arg.Any<IReadOnlyList<TaskTemplateItem>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                savedItems = call.Arg<IReadOnlyList<TaskTemplateItem>>();
                return call.Arg<TaskTemplate>();
            });

        await vm.SaveAsTemplateCommand.ExecuteAsync(null);

        await _templates.Received(1).SaveAsync(Arg.Is<TaskTemplate>(t => t.Name == "買い物"), Arg.Any<IReadOnlyList<TaskTemplateItem>>(), Arg.Any<CancellationToken>());
        Assert.NotNull(savedItems);
        Assert.Equal(["パン", "食パン 6枚切り", "卵"], savedItems.Select(i => i.Title));
        Assert.Equal([null, savedItems[0].Id, null], savedItems.Select(i => i.ParentItemId));
        Assert.Equal("テンプレート「買い物」として保存しました", vm.Notice);
        Assert.Single(_store.Lists);   // リストはそのまま残る
    }

    [Fact]
    public async Task SaveAsTemplateCommand_保存に失敗_知らせて落ちない()
    {
        var vm = Open(("卵", 0, false));
        _templates.SaveAsync(Arg.Any<TaskTemplate>(), Arg.Any<IReadOnlyList<TaskTemplateItem>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SqliteException("database is locked", 5));

        await vm.SaveAsTemplateCommand.ExecuteAsync(null);

        Assert.Equal("テンプレートにできませんでした（ログに記録しました）", vm.Notice);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void SaveAsTemplateCommand_文字のある項目が無い_押せない()
    {
        var vm = Open(("  ", 0, false));

        Assert.False(vm.SaveAsTemplateCommand.CanExecute(null));
    }
}

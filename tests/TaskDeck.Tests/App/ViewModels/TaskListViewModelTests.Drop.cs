using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>ドラッグ＆ドロップ（落とす位置 → 呼ぶメソッドと引数。UI 設計書 15.5）。</summary>
public partial class TaskListViewModelTests
{
    private async Task<(TaskListViewModel List, TaskItem A, TaskItem B, TaskItem C)> ThreeRootsAsync()
    {
        var a = _f.NewTask("A");
        var b = _f.NewTask("B");
        var c = _f.NewTask("C");
        _f.AddRow(a);
        _f.AddRow(b);
        _f.AddRow(c);
        return (await ShowAllAsync(), a, b, c);
    }

    // ---- 落とせるか・ゴーストの文言 ----

    [Fact]
    public async Task PlanDrop_手動順で行の前_何番目に入るかを出す()
    {
        var (list, _, b, c) = await ThreeRootsAsync();
        var payload = list.BeginDrag(RowOf(list, c))!;

        var plan = list.PlanDrop(payload, RowOf(list, b), DropPosition.Before);

        Assert.Equal(new DropPlan(DropPosition.Before, "2 番目へ"), plan);
    }

    [Fact]
    public async Task PlanDrop_自分自身と自分の子_落とせない()
    {
        var a = _f.NewTask("A");
        var a1 = _f.NewTask("A-1", parentId: a.Id);
        _f.AddRow(a);
        _f.AddRow(a1, isContext: true);
        var list = await ShowAllAsync();
        var payload = list.BeginDrag(RowOf(list, a))!;

        Assert.Null(list.PlanDrop(payload, RowOf(list, a), DropPosition.Into));
        Assert.Null(list.PlanDrop(payload, RowOf(list, a1), DropPosition.Into));
    }

    [Fact]
    public async Task PlanDrop_期限順の一覧_並び替えはできず子にはできる()
    {
        var a = _f.NewTask("A", Local(9, 22));
        var b = _f.NewTask("B", Local(9, 22));
        _f.AddRow(a);
        _f.AddRow(b);
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);
        var payload = list.BeginDrag(RowOf(list, b))!;

        Assert.Null(list.PlanDrop(payload, RowOf(list, a), DropPosition.Before));
        Assert.Equal(DropPosition.Into, list.PlanDrop(payload, RowOf(list, a), DropPosition.Into)?.Position);
    }

    // ---- 並び替え ----

    [Fact]
    public async Task DropAsync_行の前_前の兄弟との間の値で並び替える()
    {
        var (list, a, b, c) = await ThreeRootsAsync();
        var payload = list.BeginDrag(RowOf(list, c))!;

        await list.DropAsync(payload, RowOf(list, b), DropPosition.Before);

        await _f.Tasks.Received(1).ReorderAsync(c.Id, SortOrderMath.Between(a.SortOrder, b.SortOrder), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DropAsync_最後の行の後ろ_末尾より後ろの値にする()
    {
        var (list, a, _, c) = await ThreeRootsAsync();
        var payload = list.BeginDrag(RowOf(list, a))!;

        await list.DropAsync(payload, RowOf(list, c), DropPosition.After);

        await _f.Tasks.Received(1).ReorderAsync(a.Id, SortOrderMath.Between(c.SortOrder, null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DropAsync_別の親の子の前_その親の子にして並びも決める()
    {
        var a = _f.NewTask("A");
        var a1 = _f.NewTask("A-1", parentId: a.Id);
        var b = _f.NewTask("B");
        _f.AddRow(a);
        _f.AddRow(a1, isContext: true);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        var payload = list.BeginDrag(RowOf(list, b))!;

        await list.DropAsync(payload, RowOf(list, a1), DropPosition.Before);

        await _f.Tasks.Received(1).SetParentAsync(b.Id, a.Id, SortOrderMath.Between(null, a1.SortOrder), Arg.Any<CancellationToken>());
        await _f.Tasks.DidNotReceiveWithAnyArgs().ReorderAsync(default, default, default);
    }

    [Fact]
    public async Task DropAsync_2件を最後の後ろへ_並びを保って順に入れ取り消しは一括の1段()
    {
        var (list, a, b, c) = await ThreeRootsAsync();
        list.SelectedItem = RowOf(list, a);
        list.SetSelection([RowOf(list, a), RowOf(list, b)]);
        _f.Tasks.ReorderAsync(a.Id, Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns(ViewModelFixture.Changed(a));
        _f.Tasks.ReorderAsync(b.Id, Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns(ViewModelFixture.Changed(b));
        var payload = list.BeginDrag(RowOf(list, a))!;

        await list.DropAsync(payload, RowOf(list, c), DropPosition.After);

        var first = SortOrderMath.Between(c.SortOrder, null);
        await _f.Tasks.Received(1).ReorderAsync(a.Id, first, Arg.Any<CancellationToken>());
        await _f.Tasks.Received(1).ReorderAsync(b.Id, SortOrderMath.Between(first, null), Arg.Any<CancellationToken>());
        Assert.Equal(1, _f.Stack.Count);
        var entry = _f.Stack.Peek()!;
        Assert.Equal(UndoKind.Bulk, entry.Kind);
        Assert.Equal([a.Id, b.Id], entry.Changes.TasksBefore.Select(s => s.Task.Id));
        Assert.Equal(["2 件の並びを変えました"], _f.Toasts);
    }

    // ---- 子にする ----

    [Fact]
    public async Task DropAsync_行の上_その行の子にして取り消しに積む()
    {
        var (list, a, b, _) = await ThreeRootsAsync();
        _f.Tasks.SetParentAsync(b.Id, a.Id, null, Arg.Any<CancellationToken>())
            .Returns(OperationResult<TaskMutationResult>.Success(ViewModelFixture.Changed(b)));
        var payload = list.BeginDrag(RowOf(list, b))!;

        await list.DropAsync(payload, RowOf(list, a), DropPosition.Into);

        await _f.Tasks.Received(1).SetParentAsync(b.Id, a.Id, null, Arg.Any<CancellationToken>());
        Assert.Equal("「B」を「A」のサブタスクにしました", _f.Stack.Peek()!.Label);
        Assert.Empty(_f.Toasts);
    }

    [Fact]
    public async Task DropAsync_3階層を超える_理由を知らせて取り消しに積まない()
    {
        var (list, a, b, _) = await ThreeRootsAsync();
        _f.Tasks.SetParentAsync(b.Id, a.Id, null, Arg.Any<CancellationToken>())
            .Returns(OperationResult<TaskMutationResult>.Fail("サブタスクは3階層までです。"));
        var notices = new List<string>();
        list.NoticeRequested += (_, m) => notices.Add(m);
        var payload = list.BeginDrag(RowOf(list, b))!;

        await list.DropAsync(payload, RowOf(list, a), DropPosition.Into);

        Assert.Equal(["サブタスクは3階層までです。"], notices);
        Assert.Equal(0, _f.Stack.Count);
    }

    // ---- 掴む・離す ----

    [Fact]
    public async Task BeginDrag_親と子を選んで運ぶ_子は親に付いてくるので入れない()
    {
        var a = _f.NewTask("A");
        var a1 = _f.NewTask("A-1", parentId: a.Id);
        var b = _f.NewTask("B");
        _f.AddRow(a);
        _f.AddRow(a1, isContext: true);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        list.SelectedItem = RowOf(list, a);
        list.SetSelection([RowOf(list, a), RowOf(list, a1), RowOf(list, b)]);

        var payload = list.BeginDrag(RowOf(list, a1))!;

        Assert.Equal([a.Id, b.Id], payload.TaskIds);
        Assert.Equal("2 件のタスク", payload.Title);
    }

    [Fact]
    public async Task EndDrag_運んでいた行_薄くしていたのを戻して運んでいないことにする()
    {
        var (list, a, _, _) = await ThreeRootsAsync();
        var row = RowOf(list, a);

        list.BeginDrag(row);
        Assert.True(row.IsDragging);
        Assert.NotNull(list.ActiveDrag);

        list.EndDrag();
        Assert.False(row.IsDragging);
        Assert.Null(list.ActiveDrag);
    }

    [Fact]
    public void Merge_同じタスクを何回か変えた_最初の変更前だけを残し作ったタスクの変更前は捨てる()
    {
        var a = _f.NewTask("A");
        var created = _f.NewTask("複製");
        var first = new TaskMutationResult(new ChangeSet([new TaskSnapshot(a.Clone(), [])], [created.Id], []), [a], [created]);
        var changedA = a.Clone();
        changedA.Title = "A2";
        var second = new TaskMutationResult(
            new ChangeSet([new TaskSnapshot(changedA, []), new TaskSnapshot(created.Clone(), [])], [], []), [a, created], []);

        var merged = TaskListViewModel.Merge([first, second]);

        Assert.Equal("A", Assert.Single(merged.Changes.TasksBefore).Task.Title);
        Assert.Equal([created.Id], merged.Changes.CreatedTaskIds);
    }
}

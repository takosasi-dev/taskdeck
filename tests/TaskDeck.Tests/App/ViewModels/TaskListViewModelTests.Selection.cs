using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>複数選択（Ctrl/Shift+クリック・Ctrl+A）と一括操作（S-10、取り消しは1段）。</summary>
public partial class TaskListViewModelTests
{
    /// <summary>「すべて」に A・B・C を出して、A と C を選んだ一覧。</summary>
    private async Task<(TaskListViewModel List, TaskItem A, TaskItem B, TaskItem C)> SelectAandCAsync()
    {
        var a = _f.NewTask("A");
        var b = _f.NewTask("B");
        var c = _f.NewTask("C");
        _f.AddRow(a);
        _f.AddRow(b);
        _f.AddRow(c);
        var list = await ShowAllAsync();
        list.SelectedItem = RowOf(list, a);
        list.SetSelection([RowOf(list, c), RowOf(list, a)]);
        return (list, a, b, c);
    }

    [Fact]
    public async Task SetSelection_2件_主の行を残して並び順で持ち行も選択の見た目にする()
    {
        var (list, a, b, c) = await SelectAandCAsync();

        Assert.True(list.IsMultiSelection);
        Assert.Equal("2 件", list.SelectionSummary);
        Assert.Equal(a.Id, list.SelectedRow?.Id);
        Assert.Equal([a.Id, c.Id], list.SelectedRows.Select(r => r.Id));
        Assert.True(RowOf(list, c).IsSelected);
        Assert.False(RowOf(list, b).IsSelected);
    }

    [Fact]
    public async Task SetSelection_選択が変わる_選んだ行ぜんぶを知らせる()
    {
        var a = _f.NewTask("A");
        var b = _f.NewTask("B");
        _f.AddRow(a);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        var events = new List<ListSelection>();
        list.SelectedTaskChanged += (_, s) => events.Add(s);

        list.SelectedItem = RowOf(list, a);
        list.SetSelection([RowOf(list, a), RowOf(list, b)]);

        Assert.Equal([a.Id], events[0].TaskIds);
        Assert.Equal(a.Id, events[^1].TaskId);
        Assert.Equal([a.Id, b.Id], events[^1].TaskIds);
    }

    [Fact]
    public async Task RangeTo_見出しをまたぐ_起点から順にタスク行だけを返す()
    {
        var overdue = _f.NewTask("請求書を出す", Local(9, 19));
        var today1 = _f.NewTask("会議資料", Local(9, 22));
        var today2 = _f.NewTask("買い出し", Local(9, 22));
        _f.AddRow(overdue);
        _f.AddRow(today1);
        _f.AddRow(today2);
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);
        list.SelectedItem = RowOf(list, today2);

        var range = list.RangeTo(RowOf(list, overdue));

        Assert.Equal([today2.Id, today1.Id, overdue.Id], range.Select(r => r.Id));
    }

    [Fact]
    public async Task AllRowsForSelection_主の行を先頭にして見出しと入力行を含めない()
    {
        var overdue = _f.NewTask("請求書を出す", Local(9, 19));
        var today = _f.NewTask("会議資料", Local(9, 22));
        _f.AddRow(overdue);
        _f.AddRow(today);
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);
        list.SelectedItem = RowOf(list, today);

        var all = list.AllRowsForSelection();

        Assert.Equal([today.Id, overdue.Id], all.Select(r => r.Id));
    }

    [Fact]
    public async Task ClearSelection_複数選択中_何も選ばない状態にして同期を頼む()
    {
        var (list, _, _, _) = await SelectAandCAsync();
        var synced = 0;
        list.SelectionSyncRequested += (_, _) => synced++;

        list.ClearSelectionCommand.Execute(null);

        Assert.Empty(list.SelectedRows);
        Assert.Null(list.SelectedRow);
        Assert.False(list.IsMultiSelection);
        Assert.Equal(1, synced);
    }

    [Fact]
    public async Task ReloadAsync_複数選択中_新しい行で選び直して同期を頼む()
    {
        var (list, a, _, c) = await SelectAandCAsync();
        var synced = 0;
        list.SelectionSyncRequested += (_, _) => synced++;

        await list.ReloadAsync();

        Assert.Equal([a.Id, c.Id], list.SelectedRows.Select(r => r.Id));
        Assert.Same(RowOf(list, a), list.SelectedRow);
        Assert.True(RowOf(list, c).IsSelected);
        Assert.Equal(1, synced);
    }

    // ---- 一括操作（取り消しは1段・トースト） ----

    [Fact]
    public async Task CompleteSelectedCommand_2件_まとめて完了にして一括の取り消し1段とトースト()
    {
        var (list, a, _, c) = await SelectAandCAsync();
        var result = ViewModelFixture.Changed(a);
        _f.Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>()).Returns(result);

        await list.CompleteSelectedCommand.ExecuteAsync(null);

        await _f.Tasks.Received(1).SetCompletedAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { a.Id, c.Id })), true, Arg.Any<CancellationToken>());
        Assert.Equal(["2 件を完了にしました"], _f.Toasts);
        Assert.Equal(UndoKind.Bulk, _f.Stack.Peek()!.Kind);
        Assert.Equal(1, _f.Stack.Count);

        // 元に戻すは1回でまとめて戻る
        await _f.Undo.UndoLatestAsync();
        await _f.Tasks.Received(1).ApplyUndoAsync(result.Changes, Arg.Any<CancellationToken>());
        Assert.Equal(0, _f.Stack.Count);
    }

    [Fact]
    public async Task ToggleCompleteCommand_複数選択中のSpace_選んだ行をまとめて完了にする()
    {
        var (list, a, _, c) = await SelectAandCAsync();

        await list.ToggleCompleteCommand.ExecuteAsync(list.SelectedRow);

        await _f.Tasks.Received(1).SetCompletedAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { a.Id, c.Id })), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteSelectedCommand_複数_まとめてゴミ箱へ移して一括のトースト()
    {
        var (list, a, _, c) = await SelectAandCAsync();
        _f.Tasks.SoftDeleteAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(ViewModelFixture.Changed(a));

        await list.DeleteSelectedCommand.ExecuteAsync(null);

        await _f.Tasks.Received(1).SoftDeleteAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { a.Id, c.Id })), Arg.Any<CancellationToken>());
        Assert.Equal(["2 件を削除しました"], _f.Toasts);
        Assert.Equal(UndoKind.Bulk, _f.Stack.Peek()!.Kind);
    }

    [Fact]
    public async Task SetPriorityForSelectedAsync_複数_全部に同じ優先度を入れて一括で積む()
    {
        var (list, a, b, c) = await SelectAandCAsync();
        Action<TaskItem>? mutate = null;
        _f.Tasks.UpdateManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Do<Action<TaskItem>>(m => mutate = m), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(a));

        await list.SetPriorityForSelectedAsync(Priority.High);

        await _f.Tasks.Received(1).UpdateManyAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { a.Id, c.Id })), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>());
        mutate!(b);
        Assert.Equal(Priority.High, b.Priority);
        Assert.Equal(["2 件の優先度を「高」にしました"], _f.Toasts);
    }

    [Fact]
    public async Task SetDueForSelectedAsync_日付だけ_その日のローカル0時で日付のみにする()
    {
        var (list, a, b, _) = await SelectAandCAsync();
        Action<TaskItem>? mutate = null;
        _f.Tasks.UpdateManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Do<Action<TaskItem>>(m => mutate = m), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(a));

        await list.SetDueForSelectedAsync(new DateOnly(2026, 9, 25), null);

        mutate!(b);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 25), b.DueAt);
        Assert.False(b.DueHasTime);
        Assert.Equal(["2 件の期限を変えました"], _f.Toasts);
    }

    [Fact]
    public async Task MoveSelectedToProjectAsync_1件_タイトルとプロジェクト名のトーストで単発として積む()
    {
        var project = new Project { Name = "業務改善" };
        _f.ProjectList.Add(project);
        var a = _f.NewTask("会議資料");
        _f.AddRow(a);
        var list = await ShowAllAsync();
        list.SelectedItem = RowOf(list, a);
        _f.Tasks.UpdateManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(a));

        await list.MoveSelectedToProjectAsync(project.Id);

        Assert.Equal(["「会議資料」を「業務改善」へ移しました"], _f.Toasts);
        Assert.Equal(UndoKind.Other, _f.Stack.Peek()!.Kind);
    }

    [Fact]
    public async Task AddTagByNameToSelectedAsync_無いタグ_作ってから選んだ行ぜんぶに付ける()
    {
        var (list, a, _, c) = await SelectAandCAsync();
        var tag = new Tag { Name = "仕事" };
        _f.Tags.GetOrCreateAsync("仕事", Arg.Any<CancellationToken>()).Returns(tag);
        _f.Tasks.AddTagsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(a));

        await list.AddTagByNameToSelectedAsync("＃仕事");

        await _f.Tasks.Received(1).AddTagsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { a.Id, c.Id })),
            Arg.Is<IReadOnlyList<Guid>>(tags => tags.Single() == tag.Id),
            Arg.Any<CancellationToken>());
        Assert.Equal(["2 件にタグ「仕事」を付けました"], _f.Toasts);
    }

    [Fact]
    public async Task IndentSelectedAsync_複数選択中_階層は変えない()
    {
        var (list, _, _, _) = await SelectAandCAsync();

        await list.IndentSelectedAsync();

        await _f.Tasks.DidNotReceiveWithAnyArgs().SetParentAsync(default, default, default, default);
    }
}

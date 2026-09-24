using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>階層の操作（Tab / Shift+Tab の親の決め方 F-025、親の完了の確認 F-024、進捗 F-023）。</summary>
public partial class TaskListViewModelTests
{
    private async Task<TaskListViewModel> ShowAllAsync()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.All);
        return list;
    }

    private static TaskRowViewModel RowOf(TaskListViewModel list, TaskItem task) =>
        ViewModelFixture.TaskRows(list).Single(r => r.Id == task.Id);

    // ---- Tab: 1段下げる ----

    [Fact]
    public async Task IndentSelectedAsync_前に兄弟がある_その兄弟の末尾の子にする()
    {
        var a = _f.NewTask("A");
        var b = _f.NewTask("B");
        _f.AddRow(a);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        list.SelectedItem = RowOf(list, b);

        await list.IndentSelectedAsync();

        await _f.Tasks.Received(1).SetParentAsync(b.Id, a.Id, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IndentSelectedAsync_前の兄弟に子がいる_子を飛ばして兄弟の下に入れる()
    {
        var a = _f.NewTask("A");
        var a1 = _f.NewTask("A-1", parentId: a.Id);
        var b = _f.NewTask("B");
        _f.AddRow(a);
        _f.AddRow(a1, isContext: true);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        list.SelectedItem = RowOf(list, b);

        await list.IndentSelectedAsync();

        await _f.Tasks.Received(1).SetParentAsync(b.Id, a.Id, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IndentSelectedAsync_兄弟の中で先頭_下げずに理由を知らせる()
    {
        var a = _f.NewTask("A");
        var a1 = _f.NewTask("A-1", parentId: a.Id);
        _f.AddRow(a);
        _f.AddRow(a1, isContext: true);
        var list = await ShowAllAsync();
        var notices = new List<string>();
        list.NoticeRequested += (_, m) => notices.Add(m);
        list.SelectedItem = RowOf(list, a1);

        await list.IndentSelectedAsync();

        await _f.Tasks.DidNotReceiveWithAnyArgs().SetParentAsync(default, default, default, default);
        Assert.Single(notices);
    }

    [Fact]
    public async Task IndentSelectedAsync_3階層を超える_リポジトリの理由を知らせ取り消しに積まない()
    {
        var a = _f.NewTask("A");
        var b = _f.NewTask("B");
        _f.AddRow(a);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        _f.Tasks.SetParentAsync(b.Id, a.Id, null, Arg.Any<CancellationToken>())
            .Returns(OperationResult<TaskMutationResult>.Fail("サブタスクは3階層までです。"));
        var notices = new List<string>();
        list.NoticeRequested += (_, m) => notices.Add(m);
        list.SelectedItem = RowOf(list, b);

        await list.IndentSelectedAsync();

        Assert.Equal(["サブタスクは3階層までです。"], notices);
        Assert.Equal(0, _f.Stack.Count);
    }

    [Fact]
    public async Task IndentSelectedAsync_成功_取り消しに積む()
    {
        var a = _f.NewTask("A");
        var b = _f.NewTask("B");
        _f.AddRow(a);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        _f.Tasks.SetParentAsync(b.Id, a.Id, null, Arg.Any<CancellationToken>())
            .Returns(OperationResult<TaskMutationResult>.Success(ViewModelFixture.Changed(b)));
        list.SelectedItem = RowOf(list, b);

        await list.IndentSelectedAsync();

        Assert.Equal("「B」を1段下げました", _f.Stack.Peek()!.Label);
    }

    [Fact]
    public async Task IndentSelectedAsync_検索中_階層が見えないので何もしない()
    {
        var a = _f.NewTask("A");
        var b = _f.NewTask("AB");
        _f.AddRow(a);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        await list.SetSearchAsync("A");
        list.SelectedItem = RowOf(list, b);

        await list.IndentSelectedAsync();

        await _f.Tasks.DidNotReceiveWithAnyArgs().SetParentAsync(default, default, default, default);
    }

    // ---- Shift+Tab: 1段上げる ----

    [Fact]
    public async Task OutdentSelectedAsync_子_親の兄弟にして親と次の兄弟の間に置く()
    {
        var a = _f.NewTask("A");
        var a1 = _f.NewTask("A-1", parentId: a.Id);
        var b = _f.NewTask("B");
        _f.AddRow(a);
        _f.AddRow(a1, isContext: true);
        _f.AddRow(b);
        var list = await ShowAllAsync();
        list.SelectedItem = RowOf(list, a1);

        await list.OutdentSelectedAsync();

        await _f.Tasks.Received(1).SetParentAsync(a1.Id, null, SortOrderMath.Between(a.SortOrder, b.SortOrder), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutdentSelectedAsync_孫_祖父の子にして親のすぐ後ろに置く()
    {
        var a = _f.NewTask("A");
        var a1 = _f.NewTask("A-1", parentId: a.Id);
        var a11 = _f.NewTask("A-1-1", parentId: a1.Id);
        a11.Depth = 2;
        _f.AddRow(a);
        _f.AddRow(a1, isContext: true);
        _f.AddRow(a11, isContext: true);
        var list = await ShowAllAsync();
        list.SelectedItem = RowOf(list, a11);

        await list.OutdentSelectedAsync();

        // 親（A-1）の後ろに兄弟が出ていないので、親の値から Step の半分だけ後ろ
        await _f.Tasks.Received(1).SetParentAsync(
            a11.Id, a.Id, SortOrderMath.Between(a1.SortOrder, a1.SortOrder + SortOrderMath.Step), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutdentSelectedAsync_いちばん上の階層_上げずに理由を知らせる()
    {
        var a = _f.NewTask("A");
        _f.AddRow(a);
        var list = await ShowAllAsync();
        var notices = new List<string>();
        list.NoticeRequested += (_, m) => notices.Add(m);
        list.SelectedItem = RowOf(list, a);

        await list.OutdentSelectedAsync();

        await _f.Tasks.DidNotReceiveWithAnyArgs().SetParentAsync(default, default, default, default);
        Assert.Single(notices);
    }

    // ---- 親の完了（F-024）と進捗（F-023） ----

    [Fact]
    public async Task SetCompletedAsync_未完了の子がいる親ではいを選ぶ_子もまとめて完了にする()
    {
        var parent = _f.NewTask("会議資料まとめる");
        var done = _f.NewTask("素材を集める", status: TaskItemStatus.Completed, parentId: parent.Id);
        var open = _f.NewTask("グラフを作る", parentId: parent.Id);
        _f.AddRow(parent, subtasks: 2, subtasksDone: 1);
        _f.GivenChildren(parent, done, open);
        var list = await ShowAllAsync();
        var asked = new List<ConfirmRequest>();
        list.Confirm = request =>
        {
            asked.Add(request);
            return ConfirmChoice.Yes;
        };

        await list.SetCompletedAsync(RowOf(list, parent), completed: true);

        Assert.Single(asked);
        await _f.Tasks.Received(1).SetCompletedAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { parent.Id, open.Id })), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetCompletedAsync_未完了の子がいる親でいいえを選ぶ_親だけ完了にする()
    {
        var parent = _f.NewTask("会議資料まとめる");
        var open = _f.NewTask("グラフを作る", parentId: parent.Id);
        _f.AddRow(parent, subtasks: 1);
        _f.GivenChildren(parent, open);
        var list = await ShowAllAsync();
        list.Confirm = _ => ConfirmChoice.No;

        await list.SetCompletedAsync(RowOf(list, parent), completed: true);

        await _f.Tasks.Received(1).SetCompletedAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { parent.Id })), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetCompletedAsync_未完了の子がいる親でキャンセル_何もせずfalseを返す()
    {
        var parent = _f.NewTask("会議資料まとめる");
        var open = _f.NewTask("グラフを作る", parentId: parent.Id);
        _f.AddRow(parent, subtasks: 1);
        _f.GivenChildren(parent, open);
        var list = await ShowAllAsync();
        list.Confirm = _ => ConfirmChoice.Cancel;

        var applied = await list.SetCompletedAsync(RowOf(list, parent), completed: true);

        Assert.False(applied);
        await _f.Tasks.DidNotReceiveWithAnyArgs().SetCompletedAsync(default!, default, default);
    }

    [Fact]
    public async Task SetCompletedAsync_子が全部済んでいる親_確認しない()
    {
        var parent = _f.NewTask("会議資料まとめる");
        _f.AddRow(parent, subtasks: 2, subtasksDone: 2);
        var list = await ShowAllAsync();
        var asked = 0;
        list.Confirm = _ =>
        {
            asked++;
            return ConfirmChoice.Cancel;
        };

        await list.SetCompletedAsync(RowOf(list, parent), completed: true);

        Assert.Equal(0, asked);
        await _f.Tasks.Received(1).SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowAsync_子のある親_完了数と進捗の割合を持つ()
    {
        _f.AddRow(_f.NewTask("会議資料まとめる"), subtasks: 4, subtasksDone: 1);

        var list = await ShowAllAsync();

        var row = ViewModelFixture.TaskRows(list).Single();
        Assert.Equal("1/4", row.SubtaskProgress);
        Assert.Equal(0.25, row.SubtaskRatio);
        Assert.True(row.HasOpenSubtasks);
    }
}

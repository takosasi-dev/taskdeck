using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>複製（Ctrl+D・右クリック F-016）と、右クリックの「サブタスクを追加」。</summary>
public partial class TaskListViewModelTests
{
    [Fact]
    public async Task DuplicateAsync_サブタスクなし_聞かずに複製して複製を選ぶ()
    {
        var a = _f.NewTask("会議資料");
        _f.AddRow(a);
        var list = await ShowAllAsync();
        var copy = _f.NewTask("会議資料");
        _f.Tasks.DuplicateAsync(a.Id, false, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _f.AddRow(copy);
            return ViewModelFixture.Added(copy);
        });
        var asked = 0;
        list.Confirm = _ =>
        {
            asked++;
            return ConfirmChoice.Yes;
        };
        var scrolled = new List<Guid>();
        list.ScrollIntoViewRequested += (_, row) => scrolled.Add(row.Id);

        await list.DuplicateAsync(RowOf(list, a));

        Assert.Equal(0, asked);
        Assert.Equal(copy.Id, list.SelectedRow?.Id);
        Assert.Equal([copy.Id], scrolled);
        Assert.Equal("「会議資料」を複製しました", _f.Stack.Peek()!.Label);
        Assert.Equal(UndoKind.Other, _f.Stack.Peek()!.Kind);
    }

    [Theory]
    [InlineData(ConfirmChoice.Yes, true)]
    [InlineData(ConfirmChoice.No, false)]
    public async Task DuplicateAsync_サブタスクあり_含めるかを聞いた答えで複製する(ConfirmChoice answer, bool includeSubtasks)
    {
        var a = _f.NewTask("会議資料");
        _f.AddRow(a, subtasks: 2);
        var list = await ShowAllAsync();
        var asked = new List<ConfirmRequest>();
        list.Confirm = request =>
        {
            asked.Add(request);
            return answer;
        };

        await list.DuplicateAsync(RowOf(list, a));

        Assert.Single(asked);
        await _f.Tasks.Received(1).DuplicateAsync(a.Id, includeSubtasks, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DuplicateAsync_サブタスクありでキャンセル_複製しない()
    {
        var a = _f.NewTask("会議資料");
        _f.AddRow(a, subtasks: 1);
        var list = await ShowAllAsync();
        list.Confirm = _ => ConfirmChoice.Cancel;

        await list.DuplicateAsync(RowOf(list, a));

        await _f.Tasks.DidNotReceiveWithAnyArgs().DuplicateAsync(default, default, default);
    }

    [Fact]
    public async Task RequestSubtaskInput_複数選択中の行_その行だけを選んでサブタスクの欄を頼む()
    {
        var (list, a, _, c) = await SelectAandCAsync();
        var requested = 0;
        list.SubtaskInputRequested += (_, _) => requested++;

        list.RequestSubtaskInput(RowOf(list, c));

        Assert.Equal(1, requested);
        Assert.Equal([c.Id], list.SelectedRows.Select(r => r.Id));
        Assert.False(RowOf(list, a).IsSelected);
    }

    [Fact]
    public async Task RequestSubtaskInput_3階層目の行_頼まない()
    {
        var a = _f.NewTask("A");
        a.Depth = TaskItem.MaxDepth;
        _f.AddRow(a);
        var list = await ShowAllAsync();
        var requested = 0;
        list.SubtaskInputRequested += (_, _) => requested++;

        list.RequestSubtaskInput(RowOf(list, a));

        Assert.Equal(0, requested);
    }
}

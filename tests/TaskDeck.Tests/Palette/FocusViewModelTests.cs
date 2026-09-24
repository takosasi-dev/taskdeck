using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views.Focus;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Palette;

public class FocusViewModelTests
{
    private readonly PaletteFixture _f = new();

    private async Task<FocusViewModel> OpenAsync()
    {
        var focus = _f.CreateFocus();
        await focus.InitializeAsync();
        return focus;
    }

    /// <summary>今出している1件から順に、後ろに回しながら全部のタイトルを集める。</summary>
    private static async Task<List<string>> WalkAsync(FocusViewModel focus)
    {
        var titles = new List<string>();
        for (var i = 0; i < focus.TotalCount - focus.DoneCount; i++)
        {
            titles.Add(focus.Current!.Title);
            await focus.SkipAsync();
        }
        return titles;
    }

    private void GivenCompletes(TaskItem task, params TaskItem[] created) =>
        _f.Tasks.SetCompletedAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(task.Id)), true, Arg.Any<CancellationToken>())
            .Returns(PaletteFixture.Changed(task, created));

    [Fact]
    public async Task InitializeAsync_TodayTasks_NearestDeadlineThenHigherPriority()
    {
        _f.AddTask("明日の分", new DateTime(2026, 9, 23));
        _f.AddTask("今日中・低", new DateTime(2026, 9, 22), priority: Priority.Low);
        _f.AddTask("15時", new DateTime(2026, 9, 22, 15, 0, 0), hasTime: true);
        _f.AddTask("今日中・高", new DateTime(2026, 9, 22), priority: Priority.High);
        _f.AddTask("11時", new DateTime(2026, 9, 22, 11, 0, 0), hasTime: true, priority: Priority.Low);
        _f.AddTask("期限切れ", new DateTime(2026, 9, 20), priority: Priority.Low);

        var focus = await OpenAsync();

        Assert.Equal(["期限切れ", "11時", "15時", "今日中・高", "今日中・低"], await WalkAsync(focus));
        await _f.Tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!);
    }

    [Fact]
    public async Task InitializeAsync_DoneToday_CountsOnlyCompletedTodayTasks()
    {
        _f.AddTask("今日済ませた", new DateTime(2026, 9, 22), status: TaskItemStatus.Completed, completedUtc: FixedClock.LocalToUtc(2026, 9, 22, 9, 0));
        _f.AddTask("明日の分を先に", new DateTime(2026, 9, 23), status: TaskItemStatus.Completed, completedUtc: FixedClock.LocalToUtc(2026, 9, 22, 9, 0));
        _f.AddTask("やめた", new DateTime(2026, 9, 22), status: TaskItemStatus.Cancelled, completedUtc: FixedClock.LocalToUtc(2026, 9, 22, 9, 0));
        _f.AddTask("昨日済ませた", new DateTime(2026, 9, 21), status: TaskItemStatus.Completed, completedUtc: FixedClock.LocalToUtc(2026, 9, 21, 20, 0));
        _f.AddTask("残り1", new DateTime(2026, 9, 22));
        _f.AddTask("残り2", new DateTime(2026, 9, 22));

        var focus = await OpenAsync();

        Assert.Equal(1, focus.DoneCount);
        Assert.Equal(3, focus.TotalCount);
        Assert.Equal("2 件目 / 3 件", focus.PositionText);
        Assert.Equal("今日 ・ 残り 1 件", focus.RemainingText);
        Assert.Equal(1d / 3, focus.Progress, 6);
        Assert.Equal("今日の完了率 33%", focus.ProgressText);
    }

    [Fact]
    public async Task InitializeAsync_NothingToday_IsEmpty()
    {
        _f.AddTask("明日の分", new DateTime(2026, 9, 23));

        var focus = await OpenAsync();

        Assert.True(focus.IsEmpty);
        Assert.False(focus.IsFinished);
        Assert.Null(focus.Current);
    }

    [Fact]
    public async Task InitializeAsync_Card_ShowsProjectTagsAndNotesHead()
    {
        var project = _f.AddProject("業務改善", "#0067C0");
        var tag = _f.AddTag("仕事");
        _f.AddTask("会議資料まとめる", new DateTime(2026, 9, 22, 15, 0, 0), hasTime: true, priority: Priority.High,
            projectId: project.Id, tagIds: [tag.Id], notes: "  前四半期の数字を入れる。  ");

        var card = (await OpenAsync()).Current!;

        Assert.Equal("業務改善", card.ProjectName);
        Assert.Equal(ProjectPalette.ForTheme("#0067C0", dark: true), card.ProjectColorHex);
        Assert.Equal("#仕事", card.TagsText);
        Assert.Equal("!!!", card.PrioritySymbol);
        Assert.Equal("前四半期の数字を入れる。", card.NotesHead);
        Assert.Equal("15:00 まで", card.DueLabel);
        Assert.Equal("あと 5 時間", card.RemainingLabel);
    }

    [Fact]
    public async Task CompleteAsync_OpenTask_CompletesShowsNextAndRecordsUndoWithoutToast()
    {
        var first = _f.AddTask("先", new DateTime(2026, 9, 21));
        _f.AddTask("次", new DateTime(2026, 9, 22));
        GivenCompletes(first);
        var focus = await OpenAsync();

        await focus.CompleteAsync();

        await _f.Tasks.Received(1).SetCompletedAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == first.Id), true, Arg.Any<CancellationToken>());
        Assert.Equal("次", focus.Current?.Title);
        Assert.Equal(1, focus.DoneCount);
        Assert.Equal("2 件目 / 2 件", focus.PositionText);
        Assert.Equal(UndoKind.Toggle, _f.Stack.Peek()?.Kind);
        Assert.Empty(_f.Toasts);
        Assert.Equal("「先」を完了しました", focus.Notice);
        Assert.True(focus.CanUndo);
    }

    [Fact]
    public async Task CompleteAsync_Recurring_ToastsNextDue()
    {
        var task = _f.AddTask("日報", new DateTime(2026, 9, 22));
        GivenCompletes(task, new TaskItem { Title = "日報", DueAt = FixedClock.LocalToUtc(2026, 9, 23) });
        var focus = await OpenAsync();

        await focus.CompleteAsync();

        Assert.Equal(["完了しました・次回は 9月23日（水）"], _f.Toasts);
    }

    [Fact]
    public async Task CompleteAsync_LastTask_IsFinishedWithFullProgress()
    {
        var task = _f.AddTask("これだけ", new DateTime(2026, 9, 22));
        GivenCompletes(task);
        var focus = await OpenAsync();

        await focus.CompleteAsync();

        Assert.True(focus.IsFinished);
        Assert.Null(focus.Current);
        Assert.Equal(1d, focus.Progress);
        Assert.Equal("今日は 1 件を完了しました", focus.FinishedText);
    }

    [Fact]
    public async Task CompleteAsync_ParentWithOpenSubtaskAndCancel_KeepsTask()
    {
        var parent = _f.AddTask("資料を作る", new DateTime(2026, 9, 22));
        _f.AddTask("グラフ", parentId: parent.Id);
        var focus = await OpenAsync();
        focus.Confirm = _ => ConfirmChoice.Cancel;

        await focus.CompleteAsync();

        await _f.Tasks.DidNotReceiveWithAnyArgs().SetCompletedAsync(default!, default);
        Assert.Equal("資料を作る", focus.Current?.Title);
        Assert.Equal(0, focus.DoneCount);
    }

    [Fact]
    public async Task CompleteAsync_ParentAndYes_TakesSubtaskOutOfQueueToo()
    {
        var parent = _f.AddTask("資料を作る", new DateTime(2026, 9, 22), priority: Priority.High);
        var child = _f.AddTask("グラフ", new DateTime(2026, 9, 22), parentId: parent.Id);
        _f.Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>()).Returns(PaletteFixture.Changed(parent));
        var focus = await OpenAsync();
        focus.Confirm = _ => ConfirmChoice.Yes;

        await focus.CompleteAsync();

        await _f.Tasks.Received(1).SetCompletedAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(parent.Id) && ids.Contains(child.Id)), true, Arg.Any<CancellationToken>());
        Assert.Equal(2, focus.DoneCount);
        Assert.True(focus.IsFinished);
    }

    [Fact]
    public async Task SkipAsync_MovesCurrentToEndWithoutChangingDue()
    {
        _f.AddTask("A", new DateTime(2026, 9, 20));
        _f.AddTask("B", new DateTime(2026, 9, 21));
        _f.AddTask("C", new DateTime(2026, 9, 22));
        var focus = await OpenAsync();

        await focus.SkipAsync();
        var afterSkip = focus.Current?.Title;

        Assert.Equal("B", afterSkip);
        Assert.Equal(["B", "C", "A"], await WalkAsync(focus));
        Assert.Equal("1 件目 / 3 件", focus.PositionText);
        await _f.Tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!);
    }

    [Fact]
    public async Task SkipAsync_OnlyOneTask_StaysWithNotice()
    {
        _f.AddTask("これだけ", new DateTime(2026, 9, 22));
        var focus = await OpenAsync();

        await focus.SkipAsync();

        Assert.Equal("これだけ", focus.Current?.Title);
        Assert.Equal("ほかに今日のタスクはありません", focus.Notice);
    }

    [Fact]
    public async Task UndoAsync_AfterComplete_PutsTaskBackAtFront()
    {
        var first = _f.AddTask("先", new DateTime(2026, 9, 21));
        _f.AddTask("次", new DateTime(2026, 9, 22));
        GivenCompletes(first);
        var focus = await OpenAsync();
        await focus.CompleteAsync();

        await focus.UndoAsync();

        await _f.Tasks.Received(1).ApplyUndoAsync(Arg.Any<ChangeSet>(), Arg.Any<CancellationToken>());
        Assert.Equal("先", focus.Current?.Title);
        Assert.Equal(0, focus.DoneCount);
        Assert.False(focus.CanUndo);
    }

    [Fact]
    public async Task UndoAsync_AfterAnotherOperation_DoesNothing()
    {
        var first = _f.AddTask("先", new DateTime(2026, 9, 21));
        _f.AddTask("次", new DateTime(2026, 9, 22));
        GivenCompletes(first);
        var focus = await OpenAsync();
        await focus.CompleteAsync();
        _f.Undo.Record(PaletteFixture.Changed(new TaskItem { Title = "別の画面で" }), "別の操作", UndoKind.Other, showToast: false);

        await focus.UndoAsync();

        await _f.Tasks.DidNotReceiveWithAnyArgs().ApplyUndoAsync(default!);
        Assert.Equal("次", focus.Current?.Title);
    }

    [Fact]
    public async Task CompleteAsync_NextWasClosedElsewhere_SkipsItAndCountsIt()
    {
        var first = _f.AddTask("A", new DateTime(2026, 9, 20));
        var second = _f.AddTask("B", new DateTime(2026, 9, 21));
        _f.AddTask("C", new DateTime(2026, 9, 22));
        GivenCompletes(first);
        var focus = await OpenAsync();
        second.Status = TaskItemStatus.Completed;
        second.CompletedAt = PaletteFixture.Now;

        await focus.CompleteAsync();

        Assert.Equal("C", focus.Current?.Title);
        Assert.Equal(2, focus.DoneCount);
        Assert.Equal(3, focus.TotalCount);
    }

    [Fact]
    public async Task ToggleSubtaskAsync_Open_CompletesAndUpdatesProgress()
    {
        var parent = _f.AddTask("資料を作る", new DateTime(2026, 9, 22));
        var child = _f.AddTask("グラフ", parentId: parent.Id);
        _f.AddTask("見出し", parentId: parent.Id, status: TaskItemStatus.Completed);
        _f.Tasks.SetCompletedAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == child.Id), true, Arg.Any<CancellationToken>())
            .Returns(PaletteFixture.Changed(child));
        var focus = await OpenAsync();
        var card = focus.Current!;
        var before = card.SubtaskProgress;

        await focus.ToggleSubtaskAsync(card.Subtasks.Single(s => s.Title == "グラフ"));

        Assert.Equal("1 / 2", before);
        Assert.Equal("2 / 2", card.SubtaskProgress);
        Assert.False(card.HasOpenSubtasks);
        Assert.Equal(UndoKind.Toggle, _f.Stack.Peek()?.Kind);
        Assert.Equal("資料を作る", focus.Current?.Title);
    }
}

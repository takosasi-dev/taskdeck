using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Services;
using TaskDeck.App.Views.QuickInput;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Residency;

/// <summary>クイック入力（S-02、F-071〜075）。パーサは本物、リポジトリは NSubstitute。時計は JST 2026-09-22（火）10:00。</summary>
public class QuickInputViewModelTests
{
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly IProjectRepository _projects = Substitute.For<IProjectRepository>();
    private readonly ResidencyAppState _state = new();
    private readonly UndoStack _stack = new();
    private readonly UndoService _undo;
    private readonly List<NewTaskRequest> _added = [];
    private readonly QuickInputViewModel _viewModel;

    public QuickInputViewModelTests()
    {
        _undo = new UndoService(_tasks, _stack, NullLogger<UndoService>.Instance);
        _tasks.AddAsync(Arg.Any<NewTaskRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<NewTaskRequest>();
            _added.Add(request);
            var task = new TaskItem { Title = request.Title };
            return Task.FromResult(new TaskMutationResult(new ChangeSet([], [task.Id], []), [task], [task]));
        });
        _viewModel = new QuickInputViewModel(new QuickInputParser(_clock), _tasks, _projects, _state, _undo, _clock, NullLogger<QuickInputViewModel>.Instance);
    }

    private async Task TypeAndSubmitAsync(string text)
    {
        _viewModel.Text = text;
        _viewModel.Refresh();
        Assert.True(await _viewModel.SubmitAsync());
    }

    [Fact]
    public async Task SubmitAsync_UnreadableSyntax_StillAddsWithWholeText()
    {
        await TypeAndSubmitAsync("!!!!! 見積もりを出す");

        var request = Assert.Single(_added);
        Assert.Equal("!!!!! 見積もりを出す", request.Title);
        Assert.Equal(Priority.None, request.Priority);
    }

    [Fact]
    public async Task SubmitAsync_OnlySymbols_StillAdds()
    {
        await TypeAndSubmitAsync("@ #");

        Assert.Equal("@ #", Assert.Single(_added).Title);
    }

    [Fact]
    public async Task SubmitAsync_FullSyntax_SendsFiveElements()
    {
        await TypeAndSubmitAsync("会議資料まとめる 明日 15:00 #仕事 @業務改善 !高");

        var request = Assert.Single(_added);
        Assert.Equal("会議資料まとめる", request.Title);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, 15, 0), request.DueAt);
        Assert.True(request.DueHasTime);
        Assert.Equal(["仕事"], request.TagNames);
        Assert.Equal("業務改善", request.ProjectName);
        Assert.Equal(Priority.High, request.Priority);
    }

    [Fact]
    public async Task SubmitAsync_AddFails_KeepsInput()
    {
        _tasks.AddAsync(Arg.Any<NewTaskRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<TaskMutationResult>>(_ => throw new InvalidOperationException("DB に書けない"));
        _viewModel.Text = "消えては困る入力";

        await Assert.ThrowsAsync<InvalidOperationException>(_viewModel.SubmitAsync);

        Assert.Equal("消えては困る入力", _viewModel.Text);
        Assert.Equal(0, _stack.Count);
    }

    [Fact]
    public async Task SubmitAsync_HistoryCannotBeSaved_StillAddsAndCloses()
    {
        var state = Substitute.For<IAppStateRepository>();
        state.SetAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("AppState に書けない"));
        var viewModel = new QuickInputViewModel(new QuickInputParser(_clock), _tasks, _projects, state, _undo, _clock, NullLogger<QuickInputViewModel>.Instance);
        viewModel.Text = "履歴は無理でも登録する";

        Assert.True(await viewModel.SubmitAsync());

        Assert.Equal("履歴は無理でも登録する", Assert.Single(_added).Title);
    }

    [Fact]
    public async Task SubmitAsync_Whitespace_AddsNothing()
    {
        _viewModel.Text = "   ";

        Assert.False(await _viewModel.SubmitAsync());
        Assert.Empty(_added);
    }

    [Fact]
    public async Task SubmitAsync_Added_RecordsUndoWithoutToast()
    {
        await TypeAndSubmitAsync("牛乳を買う");

        Assert.Equal("「牛乳を買う」を追加しました", _stack.Peek()?.Label);
    }

    [Fact]
    public async Task SubmitAsync_Added_SavesHistoryNewestFirstWithoutDuplicates()
    {
        await TypeAndSubmitAsync("A");
        await TypeAndSubmitAsync("B");
        await TypeAndSubmitAsync("A");

        Assert.Equal(["A", "B"], _viewModel.History);
        Assert.Equal("[\"A\",\"B\"]", _state.Values[AppStateKeys.QuickInputHistory]);
    }

    [Fact]
    public async Task SubmitAsync_TwelveEntries_KeepsNewestTen()
    {
        for (var i = 1; i <= 12; i++)
        {
            await TypeAndSubmitAsync($"入力{i}");
        }

        Assert.Equal(QuickInputViewModel.HistoryCapacity, _viewModel.History.Count);
        Assert.Equal("入力12", _viewModel.History[0]);
        Assert.Equal("入力3", _viewModel.History[^1]);
    }

    [Fact]
    public async Task Recall_UpAndDown_WalksHistoryAndReturnsToDraft()
    {
        _state.Values[AppStateKeys.QuickInputHistory] = "[\"c\",\"b\",\"a\"]";
        await _viewModel.OpenAsync();
        _viewModel.Text = "打ちかけ";

        Assert.True(_viewModel.RecallOlder());
        Assert.Equal("c", _viewModel.Text);
        Assert.True(_viewModel.RecallOlder());
        Assert.Equal("b", _viewModel.Text);
        Assert.True(_viewModel.RecallNewer());
        Assert.Equal("c", _viewModel.Text);
        Assert.True(_viewModel.RecallNewer());
        Assert.Equal("打ちかけ", _viewModel.Text);
        Assert.False(_viewModel.RecallNewer());
    }

    [Fact]
    public async Task RecallOlder_AtOldest_StaysPut()
    {
        _state.Values[AppStateKeys.QuickInputHistory] = "[\"only\"]";
        await _viewModel.OpenAsync();

        Assert.True(_viewModel.RecallOlder());
        Assert.False(_viewModel.RecallOlder());
        Assert.Equal("only", _viewModel.Text);
    }

    [Fact]
    public async Task OpenAsync_BrokenHistory_StartsEmpty()
    {
        _state.Values[AppStateKeys.QuickInputHistory] = "{壊れた";

        await _viewModel.OpenAsync();

        Assert.Empty(_viewModel.History);
    }

    [Fact]
    public void Refresh_ParsedText_ShowsChipsLikeTheMockup()
    {
        _viewModel.Text = "会議資料まとめる 明日 15:00 #仕事 !高";

        _viewModel.Refresh();

        Assert.Equal(
            [(QuickInputChipKind.Due, "9/23（水） 15:00", (string?)null), (QuickInputChipKind.Tag, "仕事", "#"), (QuickInputChipKind.Priority, "高", "!!!")],
            _viewModel.Chips.Select(c => (c.Kind, c.Text, c.Symbol)));
        Assert.True(_viewModel.ShowFixHint);
        Assert.False(_viewModel.ShowExamples);
    }

    [Fact]
    public void Refresh_PlainText_ShowsExamples()
    {
        _viewModel.Text = "ただのメモ";

        _viewModel.Refresh();

        Assert.Empty(_viewModel.Chips);
        Assert.True(_viewModel.ShowExamples);
    }

    [Fact]
    public async Task CorrectPriority_OverParsed_WinsAndMarksChip()
    {
        _viewModel.Text = "資料 !高";
        _viewModel.Refresh();

        _viewModel.CorrectPriority(Priority.Low);
        Assert.True(await _viewModel.SubmitAsync());

        Assert.Equal(Priority.Low, Assert.Single(_added).Priority);
    }

    [Fact]
    public void CorrectPriority_Chip_IsMarkedCorrected()
    {
        _viewModel.Text = "資料 !高";
        _viewModel.Refresh();

        _viewModel.CorrectPriority(Priority.Urgent);

        var chip = Assert.Single(_viewModel.Chips);
        Assert.True(chip.IsCorrected);
        Assert.Equal("!!!!", chip.Symbol);
    }

    [Fact]
    public async Task CorrectDue_NewDateWithoutTime_SendsDateOnly()
    {
        _viewModel.Text = "資料 明日 15:00";
        _viewModel.Refresh();

        _viewModel.CorrectDue(new DateOnly(2026, 9, 25), null);
        Assert.True(await _viewModel.SubmitAsync());

        var request = Assert.Single(_added);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 25), request.DueAt);
        Assert.False(request.DueHasTime);
    }

    [Fact]
    public async Task CorrectDue_Cleared_SendsNoDue()
    {
        _viewModel.Text = "資料 明日";
        _viewModel.Refresh();

        _viewModel.CorrectDue(null, null);
        Assert.True(await _viewModel.SubmitAsync());

        Assert.Null(Assert.Single(_added).DueAt);
        Assert.DoesNotContain(_viewModel.Chips, c => c.Kind == QuickInputChipKind.Due);
    }

    [Fact]
    public void CurrentDue_ParsedTime_IsHandedToPicker()
    {
        _viewModel.Text = "資料 明日 15:00";

        Assert.Equal((new DateOnly(2026, 9, 23), new TimeOnly(15, 0)), _viewModel.CurrentDue());
    }

    [Fact]
    public async Task CorrectProjectAsync_ExistingProject_SendsIdInsteadOfName()
    {
        var project = new Project { Name = "私物" };
        _projects.GetAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);
        _viewModel.Text = "資料 @業務改善";
        _viewModel.Refresh();

        await _viewModel.CorrectProjectAsync(project.Id);
        Assert.True(await _viewModel.SubmitAsync());

        var request = Assert.Single(_added);
        Assert.Equal(project.Id, request.ProjectId);
        Assert.Null(request.ProjectName);
        Assert.Equal("私物", _viewModel.Chips.Single(c => c.Kind == QuickInputChipKind.Project).Text);
    }

    [Fact]
    public async Task OpenAsync_AfterCorrection_ClearsTextAndCorrections()
    {
        _viewModel.Text = "資料 !高";
        _viewModel.CorrectPriority(Priority.Low);

        await _viewModel.OpenAsync();
        _viewModel.Text = "次 !高";
        _viewModel.Refresh();

        Assert.False(_viewModel.Chips.Single().IsCorrected);
    }

    [Fact]
    public async Task UndoLastAsync_RightAfterSubmit_RemovesTheTask()
    {
        await TypeAndSubmitAsync("まちがい");
        await _viewModel.OpenAsync();

        Assert.True(await _viewModel.UndoLastAsync());

        await _tasks.Received(1).ApplyUndoAsync(Arg.Any<ChangeSet>(), Arg.Any<CancellationToken>());
        Assert.Equal("「まちがい」の追加を取り消しました", _viewModel.Notice);
        Assert.False(await _viewModel.UndoLastAsync());
    }

    [Fact]
    public async Task UndoLastAsync_AnotherOperationSince_LeavesItAlone()
    {
        await TypeAndSubmitAsync("これは残す");
        _undo.Record(new TaskMutationResult(new ChangeSet([], [Guid.NewGuid()], []), [], []), "別の操作", UndoKind.Other, showToast: false);
        _viewModel.Text = "";

        Assert.False(await _viewModel.UndoLastAsync());
        await _tasks.DidNotReceive().ApplyUndoAsync(Arg.Any<ChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UndoLastAsync_WhileTyping_LeavesItToTheTextBox()
    {
        await TypeAndSubmitAsync("まちがい");
        _viewModel.Text = "打ちかけ";

        Assert.False(await _viewModel.UndoLastAsync());
    }
}

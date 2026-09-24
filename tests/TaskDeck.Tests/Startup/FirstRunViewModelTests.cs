using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Residency;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Startup;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Tests.Residency;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Startup;

/// <summary>
/// 初回起動の3画面（S-15、F-181〜186）。ホットキーは HotkeyService の本物に偽の RegisterHotKey（FakeHotkeyApi）を渡して押す。
/// パーサは本物、リポジトリは NSubstitute。時計は JST 2026-09-22（火）10:00。
/// </summary>
public class FirstRunViewModelTests
{
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
    private readonly FakeSettingsStore _settings = new();
    private readonly FakeHotkeyApi _api = new();
    private readonly HotkeyService _hotkeys;
    private readonly List<HotkeyAction> _passedThrough = [];
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly List<NewTaskRequest> _added = [];
    private readonly UndoService _undo;
    private readonly List<string> _toasts = [];
    private readonly FirstRunViewModel _viewModel;

    public FirstRunViewModelTests()
    {
        _hotkeys = new HotkeyService(_settings, Switches.Production, _api, new InlineUiDispatcher(), NullLogger<HotkeyService>.Instance);
        _hotkeys.Start();
        _hotkeys.Pressed += (_, action) => _passedThrough.Add(action);
        _tasks.AddAsync(Arg.Any<NewTaskRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<NewTaskRequest>();
            _added.Add(request);
            var task = new TaskItem { Title = request.Title };
            return Task.FromResult(new TaskMutationResult(new ChangeSet([], [task.Id], []), [task], [task]));
        });
        _undo = new UndoService(_tasks, new UndoStack(), NullLogger<UndoService>.Instance);
        _undo.ToastRequested += (_, e) => _toasts.Add(e.Message);
        _viewModel = Create(_hotkeys);
    }

    private FirstRunViewModel Create(HotkeyService hotkeys) =>
        new(hotkeys, _settings, new QuickInputParser(_clock), _tasks, _undo, _clock, NullLogger<FirstRunViewModel>.Instance)
        {
            AdvanceDelay = TimeSpan.Zero,
        };

    private void Press(HotkeyAction action) => _api.Press((int)action);

    // ---- 画面の移り変わり ----

    [Fact]
    public void Constructor_Initially_ShowsWelcomeWithoutIntercept()
    {
        Assert.Equal(FirstRunStep.Welcome, _viewModel.Step);
        Assert.Equal([true, false, false], _viewModel.Dots);
        Assert.Equal("手順 1 / 3", _viewModel.StepName);
        Assert.Null(_hotkeys.Intercept);
    }

    [Fact]
    public void Begin_FromWelcome_ShowsHotkeyWithSettingKeys()
    {
        _viewModel.Begin();

        Assert.Equal(FirstRunStep.Hotkey, _viewModel.Step);
        Assert.Equal([false, true, false], _viewModel.Dots);
        Assert.Equal(["Ctrl", "+", "Shift", "+", "Space"], _viewModel.KeyCaps);
        Assert.Equal(3, _viewModel.KeyCount);
        Assert.False(_viewModel.KeyPressed);
        Assert.True(_viewModel.ShowWaiting);
        Assert.False(_viewModel.ShowHotkeyNote);
        Assert.NotNull(_hotkeys.Intercept);
    }

    [Fact]
    public void Next_BeforeKeyPressed_StaysOnHotkey()
    {
        _viewModel.Begin();

        _viewModel.Next();

        Assert.Equal(FirstRunStep.Hotkey, _viewModel.Step);
    }

    // ---- ② ホットキーを押す ----

    [Fact]
    public void QuickInputPressed_OnHotkey_AdvancesWithoutOpeningQuickInput()
    {
        _viewModel.Begin();

        Press(HotkeyAction.QuickInput);

        Assert.True(_viewModel.KeyPressed);
        Assert.Equal(FirstRunStep.Parse, _viewModel.Step);
        Assert.Empty(_passedThrough); // クイック入力は開かない
        Assert.Null(_hotkeys.Intercept); // ②を離れたら外す
    }

    [Fact]
    public void QuickInputPressed_WhileShowingCheck_KeepsSwallowingUntilAdvance()
    {
        _viewModel.AdvanceDelay = Timeout.InfiniteTimeSpan;
        _viewModel.Begin();

        Press(HotkeyAction.QuickInput);
        Press(HotkeyAction.QuickInput);

        Assert.True(_viewModel.KeyPressed);
        Assert.False(_viewModel.ShowWaiting);
        Assert.Equal(FirstRunStep.Hotkey, _viewModel.Step); // 緑のチェックを見せている間
        Assert.Empty(_passedThrough);

        _viewModel.Next(); // 待たずに「次へ」を押しても進める

        Assert.Equal(FirstRunStep.Parse, _viewModel.Step);
        Assert.Null(_hotkeys.Intercept);
    }

    [Fact]
    public void OtherHotkeyPressed_OnHotkey_PassesThrough()
    {
        _viewModel.Begin();

        Press(HotkeyAction.ShowMainWindow);
        Press(HotkeyAction.FocusMode);

        Assert.Equal([HotkeyAction.ShowMainWindow, HotkeyAction.FocusMode], _passedThrough);
        Assert.False(_viewModel.KeyPressed);
        Assert.Equal(FirstRunStep.Hotkey, _viewModel.Step);
    }

    [Fact]
    public void Detach_OnHotkey_ClearsInterceptAndStopsAdvancing()
    {
        _viewModel.Begin();

        _viewModel.Detach();
        Press(HotkeyAction.QuickInput);

        Assert.Null(_hotkeys.Intercept);
        Assert.Equal([HotkeyAction.QuickInput], _passedThrough); // 窓を閉じた後はふつうにクイック入力が開く
        Assert.False(_viewModel.KeyPressed);
        Assert.Equal(FirstRunStep.Hotkey, _viewModel.Step);
    }

    [Fact]
    public void Detach_AfterPressedBeforeAdvance_DoesNotAdvance()
    {
        _viewModel.AdvanceDelay = Timeout.InfiniteTimeSpan;
        _viewModel.Begin();
        Press(HotkeyAction.QuickInput);

        _viewModel.Detach();
        _viewModel.Next();

        Assert.Equal(FirstRunStep.Hotkey, _viewModel.Step);
        Assert.Null(_hotkeys.Intercept);
    }

    [Fact]
    public void GoTo_SkippingHotkey_NeverIntercepts()
    {
        _viewModel.GoTo(FirstRunStep.Parse);

        Assert.Equal(FirstRunStep.Parse, _viewModel.Step);
        Assert.Null(_hotkeys.Intercept);
    }

    [Fact]
    public void StatesChanged_KeyTakenThenChanged_ExplainsThenWaitsForNewKey()
    {
        _viewModel.Begin();
        _api.TakenKeys.Add(0x4B); // K は他のアプリが使っている

        _settings.Update(s => s.Hotkeys.QuickInput = "Ctrl+Alt+K");

        Assert.True(_viewModel.ShowHotkeyNote);
        Assert.True(_viewModel.HotkeyNoteIsProblem);
        Assert.False(_viewModel.ShowWaiting);
        Assert.Equal(["Ctrl", "+", "Alt", "+", "K"], _viewModel.KeyCaps);

        _settings.Update(s => s.Hotkeys.QuickInput = "Alt+J"); // 設定で別のキーにした

        Assert.False(_viewModel.ShowHotkeyNote);
        Assert.True(_viewModel.ShowWaiting);
        Assert.Equal(["Alt", "+", "J"], _viewModel.KeyCaps);
        Assert.Equal(2, _viewModel.KeyCount);

        Press(HotkeyAction.QuickInput);

        Assert.Equal(FirstRunStep.Parse, _viewModel.Step);
    }

    [Fact]
    public void Begin_DevelopmentWithoutHotkeys_ExplainsNotRegistered()
    {
        var hotkeys = new HotkeyService(_settings, Switches.Development, new FakeHotkeyApi(), new InlineUiDispatcher(), NullLogger<HotkeyService>.Instance);
        hotkeys.Start();
        var viewModel = Create(hotkeys);

        viewModel.Begin();

        Assert.True(viewModel.ShowHotkeyNote);
        Assert.False(viewModel.HotkeyNoteIsProblem);
        Assert.Contains("TASKDECK_DEV_HOTKEYS", viewModel.HotkeyNote, StringComparison.Ordinal);
    }

    // ---- ③ 日付の読み取り ----

    [Fact]
    public void Refresh_TomorrowWord_ShowsDateAndMarksWord()
    {
        _viewModel.GoTo(FirstRunStep.Parse);
        _viewModel.Text = "ゴミを出す 明日";

        _viewModel.Refresh();

        Assert.Equal("9/23（水）", _viewModel.DueLabel);
        var token = Assert.Single(_viewModel.DateTokens);
        Assert.Equal((6, 2, "明日"), (token.Start, token.Length, token.Text));
    }

    [Fact]
    public void Refresh_DateAndTime_ShowsTimeAndMarksBoth()
    {
        _viewModel.Text = "会議 明日 15:00";

        _viewModel.Refresh();

        Assert.Equal("9/23（水） 15:00", _viewModel.DueLabel);
        Assert.Equal(["明日", "15:00"], _viewModel.DateTokens.Select(t => t.Text));
    }

    [Fact]
    public void Refresh_NoDate_ShowsNothing()
    {
        _viewModel.Text = "牛乳を買う";

        _viewModel.Refresh();

        Assert.Null(_viewModel.DueLabel);
        Assert.False(_viewModel.HasDue);
        Assert.Empty(_viewModel.DateTokens);
    }

    [Fact]
    public async Task SubmitAsync_TextWithDate_AddsParsedTaskAndRecordsUndoWithoutToast()
    {
        _viewModel.Text = "ゴミを出す 明日";

        Assert.True(await _viewModel.SubmitAsync());

        var request = Assert.Single(_added);
        Assert.Equal("ゴミを出す", request.Title);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23), request.DueAt);
        Assert.False(request.DueHasTime);
        Assert.Equal("「ゴミを出す」を追加しました", _undo.Stack.Peek()?.Label);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task SubmitAsync_NoDate_AddsTextAsTitle()
    {
        _viewModel.Text = "牛乳を買う";

        Assert.True(await _viewModel.SubmitAsync());

        var request = Assert.Single(_added);
        Assert.Equal("牛乳を買う", request.Title);
        Assert.Null(request.DueAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SubmitAsync_Blank_AddsNothing(string text)
    {
        _viewModel.Text = text;

        Assert.False(_viewModel.CanSubmit);
        Assert.False(await _viewModel.SubmitAsync());
        Assert.Empty(_added);
    }

    [Fact]
    public async Task SubmitAsync_WhileComposing_AddsNothing()
    {
        _viewModel.Text = "ごみをだす";

        Assert.False(await _viewModel.SubmitAsync(composing: true));
        Assert.Empty(_added);
    }
}

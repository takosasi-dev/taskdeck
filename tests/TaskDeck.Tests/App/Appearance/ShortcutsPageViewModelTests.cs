using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.App.Residency;
using TaskDeck.App.Settings.Pages;
using TaskDeck.Tests.Residency;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>設定の「ショートカット」（F-123）。今のホットキーを見せ、「変更」で組み合わせを入れ直す（波3-H）。Win32 は偽物。</summary>
public class ShortcutsPageViewModelTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly FakeHotkeyApi _api = new();

    private (ShortcutsPageViewModel ViewModel, HotkeyService Hotkeys) Create(ResidencySwitches? switches = null)
    {
        var hotkeys = new HotkeyService(_settings, switches ?? Switches.Production, _api, new InlineUiDispatcher(), NullLogger<HotkeyService>.Instance);
        hotkeys.Start();
        var viewModel = new ShortcutsPageViewModel(_settings, hotkeys);
        viewModel.Attach();
        return (viewModel, hotkeys);
    }

    [Fact]
    public void QuickInput_DefaultHotkey_IsSpacedForReading() =>
        Assert.Equal("Ctrl + Shift + Space", new ShortcutsPageViewModel(new FakeSettingsStore()).QuickInput);

    [Fact]
    public void FocusMode_StoredHotkey_IsShownAsStored()
    {
        var settings = new FakeSettingsStore();
        settings.Update(s => s.Hotkeys.FocusMode = "Alt+F");

        Assert.Equal("Alt + F", new ShortcutsPageViewModel(settings).FocusMode);
    }

    [Fact]
    public void Assign_ValidCombination_SavesAndReregisters()
    {
        var (viewModel, hotkeys) = Create();
        viewModel.StartRecording(viewModel.Rows[0]);

        Assert.True(viewModel.Assign("Ctrl+Alt+K"));

        Assert.Equal("Ctrl+Alt+K", _settings.Current.Hotkeys.QuickInput);
        Assert.Equal((HotkeyGesture.Control | HotkeyGesture.Alt, 0x4Bu), _api.Registered[(int)HotkeyAction.QuickInput]);
        Assert.Equal(HotkeyState.Registered, hotkeys.StateOf(HotkeyAction.QuickInput));
        Assert.Equal("Ctrl + Alt + K", viewModel.Rows[0].Keys);
        Assert.Null(viewModel.Recording);
    }

    [Fact]
    public void StartRecording_WhileEditing_UnregistersHotkeys()
    {
        var (viewModel, _) = Create();

        viewModel.StartRecording(viewModel.Rows[1]);

        Assert.Empty(_api.Registered);
        Assert.Equal("キーを押してください", viewModel.Rows[1].Keys);
    }

    [Fact]
    public void CancelRecording_AfterStart_RestoresHotkeys()
    {
        var (viewModel, _) = Create();
        viewModel.StartRecording(viewModel.Rows[1]);

        viewModel.CancelRecording();

        Assert.Equal(3, _api.Registered.Count);
        Assert.Equal("Ctrl + Shift + T", viewModel.Rows[1].Keys);
    }

    [Theory]
    [InlineData("Shift+K")]
    [InlineData("F9")]
    public void Assign_WithoutCtrlAltWin_KeepsRecordingWithReason(string keys)
    {
        var (viewModel, _) = Create();
        var row = viewModel.Rows[0];
        viewModel.StartRecording(row);

        Assert.False(viewModel.Assign(keys));

        Assert.Equal("Ctrl+Shift+Space", _settings.Current.Hotkeys.QuickInput);
        Assert.Same(row, viewModel.Recording);
        Assert.True(row.HasProblem);
        Assert.Contains("Ctrl・Alt・Win", row.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Assign_SameAsAnother_Rejects()
    {
        var (viewModel, _) = Create();
        var row = viewModel.Rows[2];
        viewModel.StartRecording(row);

        Assert.False(viewModel.Assign("Ctrl+Shift+T"));

        Assert.Equal("「TaskDeck を前に出す」と同じキーです", row.Status);
        Assert.Equal("Ctrl+Shift+F", _settings.Current.Hotkeys.FocusMode);
    }

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Ctrl+Alt+Delete")]
    public void Assign_WindowsReservedCombination_Rejects(string keys)
    {
        var (viewModel, _) = Create();
        viewModel.StartRecording(viewModel.Rows[0]);

        Assert.False(viewModel.Assign(keys));

        Assert.Equal("Ctrl+Shift+Space", _settings.Current.Hotkeys.QuickInput);
        Assert.True(viewModel.Rows[0].HasProblem);
    }

    [Fact]
    public void Assign_UnsupportedKey_Rejects()
    {
        var (viewModel, _) = Create();
        viewModel.StartRecording(viewModel.Rows[0]);

        Assert.False(viewModel.Assign(null));
        Assert.True(viewModel.Rows[0].HasProblem);
    }

    [Fact]
    public void Rows_KeyTakenByAnotherApp_WarnsOnThatRow()
    {
        _api.TakenKeys.Add(0x46); // Ctrl+Shift+F
        var (viewModel, _) = Create();

        var row = viewModel.Rows[2];

        Assert.True(row.HasProblem);
        Assert.Contains("他のアプリ", row.Status, StringComparison.Ordinal);
        Assert.False(viewModel.Rows[0].HasStatus);
    }

    [Fact]
    public void Rows_DevelopmentFolder_ExplainsNotRegistered()
    {
        var (viewModel, _) = Create(Switches.Development);

        Assert.All(viewModel.Rows, r =>
        {
            Assert.False(r.HasProblem);
            Assert.Contains("TASKDECK_DEV_HOTKEYS=1", r.Status, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Detach_WhileRecording_RestoresHotkeys()
    {
        var (viewModel, _) = Create();
        viewModel.StartRecording(viewModel.Rows[0]);

        viewModel.Detach();

        Assert.Equal(3, _api.Registered.Count);
        Assert.Null(viewModel.Recording);
    }
}

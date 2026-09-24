using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.App.Residency;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Residency;

/// <summary>グローバルホットキーの登録・失敗の扱い・登録し直し（F-092・F-123、設計書のリスク表 #3）。Win32 は偽物。</summary>
public class HotkeyServiceTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly FakeHotkeyApi _api = new();

    private HotkeyService Create(ResidencySwitches? switches = null) =>
        new(_settings, switches ?? Switches.Production, _api, new InlineUiDispatcher(), NullLogger<HotkeyService>.Instance);

    [Fact]
    public void Start_Production_RegistersAllThree()
    {
        var service = Create();

        service.Start();

        Assert.Equal(3, _api.Registered.Count);
        Assert.Equal((HotkeyGesture.Control | HotkeyGesture.Shift, 0x20u), _api.Registered[(int)HotkeyAction.QuickInput]);
        Assert.All(Enum.GetValues<HotkeyAction>(), a => Assert.Equal(HotkeyState.Registered, service.StateOf(a)));
    }

    [Fact]
    public void Start_Development_RegistersNothing()
    {
        var service = Create(Switches.Development);

        service.Start();

        Assert.Equal(0, _api.RegisterCalls);
        Assert.False(service.IsEnabled);
        Assert.All(Enum.GetValues<HotkeyAction>(), a => Assert.Equal(HotkeyState.NotRegistered, service.StateOf(a)));
    }

    [Fact]
    public void Start_KeyTakenByAnotherApp_MarksInUseAndKeepsOthers()
    {
        _api.TakenKeys.Add(0x54); // Ctrl+Shift+T
        var service = Create();

        service.Start();

        Assert.Equal(HotkeyState.InUse, service.StateOf(HotkeyAction.ShowMainWindow));
        Assert.Equal(HotkeyState.Registered, service.StateOf(HotkeyAction.QuickInput));
        Assert.Equal(HotkeyState.Registered, service.StateOf(HotkeyAction.FocusMode));
    }

    [Fact]
    public void Start_UnreadableSetting_MarksInvalid()
    {
        _settings.Update(s => s.Hotkeys.FocusMode = "Ctrl+Nope");
        var service = Create();

        service.Start();

        Assert.Equal(HotkeyState.Invalid, service.StateOf(HotkeyAction.FocusMode));
        Assert.False(_api.Registered.ContainsKey((int)HotkeyAction.FocusMode));
    }

    [Fact]
    public void SettingsChanged_HotkeyChanged_ReregistersWithNewKey()
    {
        var service = Create();
        service.Start();
        var changes = 0;
        service.StatesChanged += (_, _) => changes++;

        _settings.Update(s => s.Hotkeys.QuickInput = "Alt+Space");

        Assert.Equal((HotkeyGesture.Alt, 0x20u), _api.Registered[(int)HotkeyAction.QuickInput]);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SettingsChanged_OtherSetting_DoesNotReregister()
    {
        var service = Create();
        service.Start();
        var calls = _api.RegisterCalls;

        _settings.Update(s => s.Appearance.CompactRows = true);

        Assert.Equal(calls, _api.RegisterCalls);
    }

    [Fact]
    public void SettingsChanged_KeyFreedByOtherApp_RegistersOnNextChange()
    {
        _api.TakenKeys.Add(0x20);
        var service = Create();
        service.Start();
        _api.TakenKeys.Clear();

        _settings.Update(s => s.Hotkeys.QuickInput = "Ctrl+Alt+Space");

        Assert.Equal(HotkeyState.Registered, service.StateOf(HotkeyAction.QuickInput));
    }

    [Fact]
    public void SetSuspended_WhileEditing_UnregistersThenRestores()
    {
        var service = Create();
        service.Start();

        service.SetSuspended(true);
        Assert.Empty(_api.Registered);
        Assert.Equal(HotkeyState.NotRegistered, service.StateOf(HotkeyAction.QuickInput));

        service.SetSuspended(false);
        Assert.Equal(3, _api.Registered.Count);
    }

    [Fact]
    public void Pressed_RegisteredId_RaisesAction()
    {
        var service = Create();
        service.Start();
        HotkeyAction? pressed = null;
        service.Pressed += (_, action) => pressed = action;

        _api.Press((int)HotkeyAction.FocusMode);

        Assert.Equal(HotkeyAction.FocusMode, pressed);
    }

    [Fact]
    public void Pressed_Intercepted_DoesNotRaiseOnlyForTakenKeys()
    {
        var service = Create();
        service.Start();
        var pressed = new List<HotkeyAction>();
        service.Pressed += (_, action) => pressed.Add(action);
        service.Intercept = action => action == HotkeyAction.QuickInput;

        _api.Press((int)HotkeyAction.QuickInput);
        _api.Press((int)HotkeyAction.FocusMode);
        service.Intercept = null;
        _api.Press((int)HotkeyAction.QuickInput);

        Assert.Equal([HotkeyAction.FocusMode, HotkeyAction.QuickInput], pressed);
    }

    [Fact]
    public void Dispose_Started_UnregistersEverything()
    {
        var service = Create();
        service.Start();

        service.Dispose();

        Assert.Empty(_api.Registered);
        Assert.True(_api.IsDisposed);
    }
}

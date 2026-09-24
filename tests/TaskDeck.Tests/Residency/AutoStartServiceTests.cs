using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.App.Residency;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Residency;

/// <summary>自動起動（F-094）。レジストリは偽物（本物の HKCU の Run には触らない）。</summary>
public class AutoStartServiceTests
{
    private const string Exe = @"C:\Tools\TaskDeck\TaskDeck.exe";

    private readonly FakeSettingsStore _settings = new();
    private readonly FakeRunKey _runKey = new();

    private AutoStartService Create(ResidencySwitches switches, string exe = Exe) =>
        new(_settings, switches, _runKey, NullLogger<AutoStartService>.Instance, exe);

    [Fact]
    public void CommandFor_Path_QuotesAndAddsTray() =>
        Assert.Equal("\"C:\\Tools\\TaskDeck\\TaskDeck.exe\" --tray", AutoStartService.CommandFor(Exe));

    [Fact]
    public void Start_LaunchAtLoginOn_WritesRunValue()
    {
        Create(Switches.Production).Start();

        Assert.Equal("\"C:\\Tools\\TaskDeck\\TaskDeck.exe\" --tray", _runKey.Values[AutoStartService.ValueName]);
    }

    [Fact]
    public void Start_ExeMoved_RewritesToCurrentPath()
    {
        _runKey.Values[AutoStartService.ValueName] = "\"D:\\old\\TaskDeck.exe\" --tray";

        Create(Switches.Production).Start();

        Assert.Equal(AutoStartService.CommandFor(Exe), _runKey.Values[AutoStartService.ValueName]);
    }

    [Fact]
    public void Start_AlreadyCurrent_DoesNotWrite()
    {
        _runKey.Values[AutoStartService.ValueName] = AutoStartService.CommandFor(Exe);

        Create(Switches.Production).Start();

        Assert.Equal(0, _runKey.Writes);
    }

    [Fact]
    public void SettingsChanged_TurnedOff_DeletesRunValue()
    {
        Create(Switches.Production).Start();

        _settings.Update(s => s.General.LaunchAtLogin = false);

        Assert.False(_runKey.Values.ContainsKey(AutoStartService.ValueName));
    }

    [Fact]
    public void SettingsChanged_UnrelatedSetting_DoesNotTouchRegistry()
    {
        Create(Switches.Production).Start();
        var writes = _runKey.Writes;

        _settings.Update(s => s.Appearance.CompactRows = true);

        Assert.Equal(writes, _runKey.Writes);
    }

    [Fact]
    public void Start_DevelopmentFolder_NeverWritesOrDeletes()
    {
        _runKey.Values[AutoStartService.ValueName] = "\"C:\\Real\\TaskDeck.exe\" --tray";
        var service = Create(Switches.Development);

        service.Start();
        _settings.Update(s => s.General.LaunchAtLogin = false);
        _settings.Update(s => s.General.LaunchAtLogin = true);

        Assert.Equal(0, _runKey.Writes);
        Assert.Equal("\"C:\\Real\\TaskDeck.exe\" --tray", _runKey.Values[AutoStartService.ValueName]);
    }
}

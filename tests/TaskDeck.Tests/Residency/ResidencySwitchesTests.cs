using TaskDeck.App;
using TaskDeck.App.Residency;

namespace TaskDeck.Tests.Residency;

/// <summary>開発用フォルダでの決まり（INTERFACES 5.9）。環境変数は読むだけで、書き換えない（他のテストと並んで走るため）。</summary>
public class ResidencySwitchesTests
{
    [Fact]
    public void From_ProductionFolder_UsesEverythingForReal()
    {
        var switches = ResidencySwitches.From(new AppPaths(@"C:\Users\someone\AppData\Local\TaskDeck", IsDevelopment: false));

        Assert.True(switches.WriteRunKey);
        Assert.True(switches.RegisterHotkeys);
        Assert.True(switches.ShowToasts);
        Assert.False(switches.NoActivate);
    }

    [Fact]
    public void From_DevelopmentFolder_NeverWritesRunKey()
    {
        var switches = ResidencySwitches.From(new AppPaths(@"L:\devdata\x", IsDevelopment: true));

        Assert.False(switches.WriteRunKey);
        Assert.Equal(Environment.GetEnvironmentVariable("TASKDECK_DEV_HOTKEYS") == "1", switches.RegisterHotkeys);
        Assert.Equal(Environment.GetEnvironmentVariable("TASKDECK_DEV_TOASTS") == "1", switches.ShowToasts);
    }
}

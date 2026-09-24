using TaskDeck.App.Residency;

namespace TaskDeck.Tests.Residency;

/// <summary>ホットキーの文字列（設定の "Ctrl+Shift+Space"）の読み書き。修飾キーの値は Win32 の MOD_*。</summary>
public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Space", HotkeyGesture.Control | HotkeyGesture.Shift, 0x20u)]
    [InlineData("Ctrl+Shift+T", HotkeyGesture.Control | HotkeyGesture.Shift, 0x54u)]
    [InlineData("Alt+F", HotkeyGesture.Alt, 0x46u)]
    [InlineData("Win+Alt+F12", HotkeyGesture.Win | HotkeyGesture.Alt, 0x7Bu)]
    [InlineData("ctrl + shift + space", HotkeyGesture.Control | HotkeyGesture.Shift, 0x20u)]
    [InlineData("Control+Windows+1", HotkeyGesture.Control | HotkeyGesture.Win, 0x31u)]
    [InlineData("Ctrl+Num5", HotkeyGesture.Control, 0x65u)]
    public void TryParse_KnownText_ReadsModifiersAndKey(string text, uint modifiers, uint key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl++")]
    [InlineData(null)]
    public void TryParse_BadText_Fails(string? text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void ToString_AnyModifierOrder_UsesCtrlAltShiftWinOrder()
    {
        Assert.True(HotkeyGesture.TryParse("Shift+Win+Alt+Ctrl+K", out var gesture));

        Assert.Equal("Ctrl+Alt+Shift+Win+K", gesture.ToString());
    }

    [Fact]
    public void ToString_Default_RoundTrips()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+Space", out var gesture));

        Assert.Equal("Ctrl+Shift+Space", gesture.ToString());
    }

    [Theory]
    [InlineData("Ctrl+Shift+Space", "Ctrl + Shift + Space")]
    [InlineData("alt+f", "Alt + F")]
    [InlineData("Ctrl+???", "Ctrl + ???")]
    public void Display_Text_IsSpacedForReading(string text, string expected) =>
        Assert.Equal(expected, HotkeyGesture.Display(text));

    [Fact]
    public void FromKey_LetterWithModifiers_BuildsGesture() =>
        Assert.Equal("Ctrl+Alt+K", HotkeyGesture.FromKey(HotkeyGesture.Control | HotkeyGesture.Alt, 0x4B)?.ToString());

    [Fact]
    public void FromKey_ModifierKeyOnly_ReturnsNull() => Assert.Null(HotkeyGesture.FromKey(HotkeyGesture.Control, 0x11)); // VK_CONTROL

    [Theory]
    [InlineData("Ctrl+Space", true)]
    [InlineData("Win+Q", true)]
    [InlineData("Shift+Q", false)]
    [InlineData("F9", false)]
    public void HasCommandModifier_NeedsCtrlAltOrWin(string text, bool expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, gesture.HasCommandModifier);
    }
}

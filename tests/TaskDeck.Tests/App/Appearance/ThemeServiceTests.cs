using TaskDeck.App.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>
/// ThemeService の画面に依存しない部分（固定アクセント色の読み替え・色の読み取り・アニメーションを減らす）。
/// 辞書の差し替えは画面が要るので、起動して確かめる（Application が無いときは辞書に触らない）。
/// </summary>
public class ThemeServiceTests
{
    [Fact]
    public void AccentForTheme_LightValueInDarkTheme_ReturnsPairedDarkValue()
    {
        foreach (var choice in ThemeService.AccentChoices)
        {
            Assert.Equal(choice.DarkHex, ThemeService.AccentForTheme(choice.LightHex, dark: true));
        }
    }

    [Fact]
    public void AccentForTheme_DarkValueInLightTheme_ReturnsPairedLightValue() =>
        Assert.Equal("#0067C0", ThemeService.AccentForTheme("#4cc2ff", dark: false));

    [Fact]
    public void AccentForTheme_ColorOutsideChoices_ReturnsAsIs() =>
        Assert.Equal("#123456", ThemeService.AccentForTheme("#123456", dark: true));

    [Fact]
    public void AccentChoices_All_AreParseableAndDistinct()
    {
        Assert.All(ThemeService.AccentChoices, c =>
        {
            Assert.NotNull(ThemeService.TryParseColor(c.LightHex));
            Assert.NotNull(ThemeService.TryParseColor(c.DarkHex));
        });
        Assert.Equal(ThemeService.AccentChoices.Count, ThemeService.AccentChoices.Select(c => c.LightHex).Distinct().Count());
    }

    /// <summary>アクセントは文字にも使う。どの面の上でも 4.5:1、アクセント地の上の文字も 4.5:1（validate-palette.js --accents と同じ基準）。</summary>
    [Theory]
    [InlineData("Light.xaml", false)]
    [InlineData("Dark.xaml", true)]
    public void AccentChoices_EachTheme_MeetTextContrastOnEverySurface(string fileName, bool dark)
    {
        var colors = RepoFiles.Colors(RepoFiles.ThemePath(fileName));
        string[] surfaces = ["SurfaceWindowColor", "SurfaceContentColor", "SurfaceSubtleColor", "SurfaceSelectedColor",
            "SurfaceHoverColor", "SettingsCardColor"];

        foreach (var choice in ThemeService.AccentChoices)
        {
            var accent = dark ? choice.DarkHex : choice.LightHex;
            foreach (var surface in surfaces)
            {
                Assert.True(ColorMath.Contrast(accent, colors[surface]) >= 4.5, $"{accent} on {surface}");
            }
            Assert.True(ColorMath.Contrast(colors["TextOnAccentColor"], accent) >= 4.5, $"Text.OnAccent on {accent}");
        }
    }

    [Theory]
    [InlineData("#0067C0")]
    [InlineData("#FF0067C0")]
    [InlineData("Red")]
    public void TryParseColor_ValidText_ReturnsColor(string text) =>
        Assert.NotNull(ThemeService.TryParseColor(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#GG0000")]
    [InlineData("青")]
    public void TryParseColor_InvalidText_ReturnsNull(string? text) =>
        Assert.Null(ThemeService.TryParseColor(text));

    [Fact]
    public void Apply_ReduceMotionOnInSettings_TurnsReduceMotionOnAndNotifies()
    {
        var settings = new FakeSettingsStore();
        settings.Update(s => s.Appearance.ReduceMotion = true);
        var theme = new ThemeService(settings);
        var notified = 0;
        theme.ReduceMotionChanged += (_, _) => notified++;

        theme.ApplyFromSettings();

        Assert.True(theme.ReduceMotion);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void Apply_ReduceMotionOffInSettings_KeepsReduceMotionOff()
    {
        var settings = new FakeSettingsStore();
        settings.Update(s => s.Appearance.ReduceMotion = false);
        var theme = new ThemeService(settings);

        theme.Apply(AppThemeMode.Dark);

        Assert.False(theme.ReduceMotion);
    }
}

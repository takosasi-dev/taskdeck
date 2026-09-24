using TaskDeck.Core.Services;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>
/// プロジェクト色（UI 設計書 2.5、open_issues 2.1）。ダークの6色はライトと同じ順・同じ役割（色相）で、明度だけ上げたもの。
/// 色覚の検証そのもの（隣接ΔE）は tools/validate-palette.js が受け持つ。ここは役割と地とのコントラストの回帰を見る。
/// </summary>
public class ProjectPaletteTests
{
    [Fact]
    public void Dark_Always_HasSameCountAsLight()
    {
        Assert.Equal(6, ProjectPalette.Light.Count);
        Assert.Equal(ProjectPalette.Light.Count, ProjectPalette.Dark.Count);
    }

    [Fact]
    public void Dark_EachColor_KeepsTheRoleOfTheLightColorAtTheSameIndex()
    {
        for (var i = 0; i < ProjectPalette.Dark.Count; i++)
        {
            var darkHue = ColorMath.Hue(ProjectPalette.Dark[i]);
            var nearest = ProjectPalette.Light
                .Select((hex, index) => (index, gap: ColorMath.HueGap(darkHue, ColorMath.Hue(hex))))
                .MinBy(x => x.gap)
                .index;

            Assert.Equal(i, nearest);
        }
    }

    [Fact]
    public void Dark_EachColor_IsLighterThanTheLightColor()
    {
        for (var i = 0; i < ProjectPalette.Dark.Count; i++)
        {
            Assert.True(
                ColorMath.OkLab(ProjectPalette.Dark[i]).L > ColorMath.OkLab(ProjectPalette.Light[i]).L,
                $"{ProjectPalette.Dark[i]} は {ProjectPalette.Light[i]} より明るいはず");
        }
    }

    [Fact]
    public void Dark_EachColor_HasThreeToOneContrastOnDarkContentSurface()
    {
        var surface = RepoFiles.Colors(RepoFiles.ThemePath("Dark.xaml"))["SurfaceContentColor"];

        Assert.All(ProjectPalette.Dark, hex => Assert.True(ColorMath.Contrast(hex, surface) >= 3, hex));
    }

    [Fact]
    public void ForTheme_DarkTheme_ReturnsDarkColorAtSameIndex()
    {
        for (var i = 0; i < ProjectPalette.Light.Count; i++)
        {
            Assert.Equal(ProjectPalette.Dark[i], ProjectPalette.ForTheme(ProjectPalette.Light[i], dark: true));
        }
    }

    [Fact]
    public void ForTheme_LowerCaseHex_IsMatchedIgnoringCase() =>
        Assert.Equal(ProjectPalette.Dark[1], ProjectPalette.ForTheme(ProjectPalette.Light[1].ToLowerInvariant(), dark: true));

    [Fact]
    public void ForTheme_LightTheme_ReturnsColorAsIs() =>
        Assert.Equal("#CA5010", ProjectPalette.ForTheme("#CA5010", dark: false));

    [Fact]
    public void ForTheme_ColorOutsidePalette_ReturnsColorAsIs() =>
        Assert.Equal("#123456", ProjectPalette.ForTheme("#123456", dark: true));
}

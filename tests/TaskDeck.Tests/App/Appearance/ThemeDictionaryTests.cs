using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Markup;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>
/// テーマ辞書（Resources/Themes/*.xaml）の約束。ThemeService は辞書を丸ごと差し替えるので、3つが同じキーを持たないと
/// 切り替えた瞬間に色が抜ける。XAML の誤りは実行時にしか出ないので、読み込めることもここで確かめる。
/// </summary>
public class ThemeDictionaryTests
{
    private static readonly string[] ThemeFiles = ["Light.xaml", "Dark.xaml", "HighContrast.xaml"];

    [Theory]
    [InlineData("Dark.xaml")]
    [InlineData("HighContrast.xaml")]
    public void Keys_OtherThemes_MatchLightExactly(string fileName)
    {
        var light = RepoFiles.Keys(RepoFiles.ThemePath("Light.xaml")).ToHashSet();
        var other = RepoFiles.Keys(RepoFiles.ThemePath(fileName)).ToHashSet();

        Assert.Empty(light.Except(other));
        Assert.Empty(other.Except(light));
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    [InlineData("HighContrast.xaml")]
    public void Keys_EachTheme_HasNoDuplicates(string fileName)
    {
        var keys = RepoFiles.Keys(RepoFiles.ThemePath(fileName));

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    /// <summary>INTERFACES 6章で他の担当に約束したキー（消さない・名前を変えない）。</summary>
    [Theory]
    [InlineData("SurfaceWindowBrush")]
    [InlineData("SurfaceContentBrush")]
    [InlineData("TextSecondaryBrush")]
    [InlineData("AccentDefaultBrush")]
    [InlineData("StateOverdueBrush")]
    [InlineData("PriorityHighBrush")]
    [InlineData("TextOnAccentBrush")]
    [InlineData("StrokeDividerBrush")]
    public void Keys_ContractColor_IsDefinedInLight(string key) =>
        Assert.Contains(key, RepoFiles.Keys(RepoFiles.ThemePath("Light.xaml")));

    [Theory]
    [InlineData("Buttons.xaml", "PrimaryButtonStyle")]
    [InlineData("Buttons.xaml", "StandardButtonStyle")]
    [InlineData("Buttons.xaml", "TextButtonStyle")]
    [InlineData("Buttons.xaml", "DangerButtonStyle")]
    [InlineData("Buttons.xaml", "IconButtonStyle")]
    [InlineData("Buttons.xaml", "AppFocusVisualStyle")]
    [InlineData("CheckBoxes.xaml", "TaskCheckBoxStyle")]
    [InlineData("CheckBoxes.xaml", "SubtaskCheckBoxStyle")]
    [InlineData("Inputs.xaml", "InputTextBoxStyle")]
    [InlineData("Inputs.xaml", "InlineTextBoxStyle")]
    [InlineData("Popups.xaml", "PopupShadowStyle")]
    [InlineData("Popups.xaml", "PopupSurfaceStyle")]
    public void Keys_ContractStyle_IsDefined(string fileName, string key) =>
        Assert.Contains(key, RepoFiles.Keys(RepoFiles.StylePath(fileName)));

    /// <summary>NFR #19。文字の色 × 文字が乗る面で 4.5:1（tools/validate-palette.js のコントラスト検査の中心部分）。</summary>
    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void Contrast_TextOnSurfaces_IsAtLeastFourPointFive(string fileName)
    {
        var colors = RepoFiles.Colors(RepoFiles.ThemePath(fileName));
        string[] texts = ["TextPrimaryColor", "TextSecondaryColor", "TextTertiaryColor", "StateOverdueColor", "PriorityUrgentColor",
            "PriorityHighColor", "PriorityMediumColor", "PriorityLowColor", "AccentDefaultColor"];
        string[] surfaces = ["SurfaceWindowColor", "SurfaceContentColor", "SurfaceSubtleColor", "SurfaceSelectedColor",
            "SurfaceHoverColor", "SettingsCardColor"];

        foreach (var text in texts)
        {
            foreach (var surface in surfaces)
            {
                var ratio = ColorMath.Contrast(colors[text], colors[surface]);
                Assert.True(ratio >= 4.5, $"{fileName}: {text} on {surface} = {ratio:0.00}");
            }
        }
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    [InlineData("HighContrast.xaml")]
    public void Load_EachTheme_ParsesAndDefinesEveryKey(string fileName)
    {
        var path = RepoFiles.ThemePath(fileName);
        var expected = RepoFiles.Keys(path);
        var loadedKeys = RunOnStaThread(() =>
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse(File.ReadAllText(path));
            return dictionary.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();
        });

        Assert.All(expected, key => Assert.Contains(key, loadedKeys));
    }

    [Fact]
    public void ThemeFiles_All_Exist() =>
        Assert.All(ThemeFiles, name => Assert.True(File.Exists(RepoFiles.ThemePath(name)), name));

    /// <summary>WPF のオブジェクトは STA のスレッドで作る。失敗はそのままテストの失敗として戻す。</summary>
    private static T RunOnStaThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
#pragma warning disable CA1031 // 別スレッドの失敗を呼び出し元のテストに戻すため（下で投げ直す）
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
#pragma warning restore CA1031
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}

using TaskDeck.App.Services;
using TaskDeck.App.Settings.Pages;
using TaskDeck.Core.Settings;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>設定の「外観」。値は変えた瞬間に ISettingsStore へ入る（保存ボタンは無い）。</summary>
public class AppearancePageViewModelTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly AppearancePageViewModel _viewModel;

    public AppearancePageViewModelTests() => _viewModel = new AppearancePageViewModel(_settings, new ThemeService(_settings));

    [Fact]
    public void IsThemeDark_SetTrue_StoresDarkAndUpdatesTheOtherChoices()
    {
        var changed = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _viewModel.IsThemeDark = true;

        Assert.Equal(AppThemeMode.Dark, _settings.Current.Appearance.Theme);
        Assert.False(_viewModel.IsThemeSystem);
        Assert.Contains(nameof(AppearancePageViewModel.IsThemeSystem), changed);
    }

    [Fact]
    public void IsThemeLight_SetFalse_DoesNotChangeTheme()
    {
        _viewModel.IsThemeLight = false;

        Assert.Equal(AppThemeMode.System, _settings.Current.Appearance.Theme);
    }

    [Fact]
    public void UseSystemAccent_SetFalse_StoresFirstFixedColor()
    {
        _viewModel.UseSystemAccent = false;

        Assert.Equal(ThemeService.AccentChoices[0].LightHex, _settings.Current.Appearance.AccentColorHex);
        Assert.Single(_viewModel.AccentSwatches, s => s.IsCurrent);
    }

    [Fact]
    public void PickAccent_Choice_StoresLightValueAndMarksItCurrent()
    {
        var purple = ThemeService.AccentChoices.Single(c => c.Name == "紫");

        _viewModel.PickAccentCommand.Execute(purple.LightHex);

        Assert.Equal(purple.LightHex, _settings.Current.Appearance.AccentColorHex);
        Assert.False(_viewModel.UseSystemAccent);
        Assert.Equal("紫", _viewModel.AccentSwatches.Single(s => s.IsCurrent).Name);
    }

    [Fact]
    public void UseSystemAccent_SetTrue_ClearsFixedColor()
    {
        _settings.Update(s => s.Appearance.AccentColorHex = "#805DB0");

        _viewModel.UseSystemAccent = true;

        Assert.Null(_settings.Current.Appearance.AccentColorHex);
        Assert.DoesNotContain(_viewModel.AccentSwatches, s => s.IsCurrent);
    }

    [Fact]
    public void AccentSwatches_StoredDarkValue_MarksTheSameChoiceCurrent()
    {
        _settings.Update(s => s.Appearance.AccentColorHex = "#4CC2FF"); // 以前の版がダークの値を保存していた場合

        Assert.Equal("青", _viewModel.AccentSwatches.Single(s => s.IsCurrent).Name);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void ReduceMotionIndex_Set_StoresNullableSetting(int index, bool? expected)
    {
        _settings.Update(s => s.Appearance.ReduceMotion = index == 0 ? true : null);

        _viewModel.ReduceMotionIndex = index;

        Assert.Equal(expected, _settings.Current.Appearance.ReduceMotion);
    }

    [Fact]
    public void ReduceMotionDescription_FollowingWindows_SaysSo()
    {
        Assert.Equal(0, _viewModel.ReduceMotionIndex);
        Assert.StartsWith("Windows の「アニメーション効果」に従っています", _viewModel.ReduceMotionDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void RowHeightIndex_SetCompact_StoresCompactRows()
    {
        _viewModel.RowHeightIndex = 1;

        Assert.True(_settings.Current.Appearance.CompactRows);
    }

    [Fact]
    public void RowHeightIndex_OutOfRange_IsIgnored()
    {
        _viewModel.RowHeightIndex = 5;

        Assert.False(_settings.Current.Appearance.CompactRows);
    }

    [Fact]
    public void AutoShowDetailPane_SetFalse_StoresFalse()
    {
        _viewModel.AutoShowDetailPane = false;

        Assert.False(_settings.Current.Appearance.AutoShowDetailPane);
    }
}

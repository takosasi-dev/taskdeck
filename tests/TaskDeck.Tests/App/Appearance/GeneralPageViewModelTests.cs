using TaskDeck.App.Settings.Pages;
using TaskDeck.Core.Queries;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>設定の「全般」（起動時のビュー F-122・週の始まり F-125・自動起動・詳しいログ）。</summary>
public class GeneralPageViewModelTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly GeneralPageViewModel _viewModel;

    public GeneralPageViewModelTests() => _viewModel = new GeneralPageViewModel(_settings);

    [Fact]
    public void StartViewIndex_SetUpcoming_StoresViewKeyString()
    {
        _viewModel.StartViewIndex = GeneralPageViewModel.StartViews.ToList().IndexOf(ViewKey.Upcoming);

        Assert.Equal(ViewKey.Upcoming.ToString(), _settings.Current.General.StartView);
    }

    [Fact]
    public void StartViewNames_BuiltInViews_UseTheirTitles() =>
        Assert.Equal(new[] { "今日", "予定", "すべて", "完了済み" }, _viewModel.StartViewNames);

    [Fact]
    public void StartViewIndex_UnknownStoredValue_FallsBackToToday()
    {
        _settings.Update(s => s.General.StartView = "Nonsense");

        Assert.Equal(0, _viewModel.StartViewIndex);
    }

    [Fact]
    public void StartViewIndex_StoredProjectView_FallsBackToToday()
    {
        _settings.Update(s => s.General.StartView = ViewKey.ForProject(Guid.NewGuid()).ToString());

        Assert.Equal(0, _viewModel.StartViewIndex);
    }

    [Fact]
    public void WeekStartIndex_SetMonday_StoresWeekStartsOnMonday()
    {
        _viewModel.WeekStartIndex = 1;

        Assert.True(_settings.Current.Calendar.WeekStartsOnMonday);
        Assert.Equal(1, _viewModel.WeekStartIndex);
    }

    [Fact]
    public void LaunchAtLogin_SetFalse_StoresFalse()
    {
        _viewModel.LaunchAtLogin = false;

        Assert.False(_settings.Current.General.LaunchAtLogin);
    }

    [Fact]
    public void VerboseLogging_SetTrue_StoresTrue()
    {
        _viewModel.VerboseLogging = true;

        Assert.True(_settings.Current.General.VerboseLogging);
    }

    [Fact]
    public void LaunchAtLoginNote_DevelopmentFolder_SaysNotRegistered()
    {
        var viewModel = new GeneralPageViewModel(_settings, TaskDeck.Tests.Residency.Switches.Development);

        Assert.True(viewModel.HasLaunchAtLoginNote);
        Assert.Contains("Windows には登録しません", viewModel.LaunchAtLoginNote, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchAtLoginNote_Production_IsEmpty() =>
        Assert.False(new GeneralPageViewModel(_settings, TaskDeck.Tests.Residency.Switches.Production).HasLaunchAtLoginNote);
}

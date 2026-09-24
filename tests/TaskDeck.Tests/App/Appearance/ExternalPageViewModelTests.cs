using TaskDeck.App.Settings.Pages;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>設定の「外部サービス」。波1は値の保存だけ。URL のタイトル取得は既定オフ（URL が外部に送られるため）。</summary>
public class ExternalPageViewModelTests
{
    private readonly FakeSettingsStore _settings = new();
    private readonly ExternalPageViewModel _viewModel;

    public ExternalPageViewModelTests() => _viewModel = new ExternalPageViewModel(_settings);

    [Fact]
    public void LinkPreviewEnabled_Default_IsOff() => Assert.False(_viewModel.LinkPreviewEnabled);

    [Fact]
    public void OfflineMode_SetTrue_StoresAndTurnsOnlineItemsOff()
    {
        var changed = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _viewModel.OfflineMode = true;

        Assert.True(_settings.Current.External.OfflineMode);
        Assert.False(_viewModel.IsOnline);
        Assert.Contains(nameof(ExternalPageViewModel.IsOnline), changed);
    }

    [Fact]
    public void HolidaysEnabled_SetFalse_StoresFalse()
    {
        _viewModel.HolidaysEnabled = false;

        Assert.False(_settings.Current.External.HolidaysEnabled);
    }

    [Fact]
    public void ShiftWeekdayRecurrenceOnHolidays_SetFalse_StoresFalse()
    {
        _viewModel.ShiftWeekdayRecurrenceOnHolidays = false;

        Assert.False(_settings.Current.External.ShiftWeekdayRecurrenceOnHolidays);
    }

    [Fact]
    public void LinkPreviewEnabled_SetTrue_StoresTrue()
    {
        _viewModel.LinkPreviewEnabled = true;

        Assert.True(_settings.Current.External.LinkPreviewEnabled);
    }

    [Fact]
    public void WeatherLocationName_NoLocation_ExplainsThatItIsUnset() =>
        Assert.Contains("未設定", _viewModel.WeatherLocationName, StringComparison.Ordinal);
}

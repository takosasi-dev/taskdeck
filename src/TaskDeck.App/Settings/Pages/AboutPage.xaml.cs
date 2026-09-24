using System.Windows;
using System.Windows.Controls;

namespace TaskDeck.App.Settings.Pages;

/// <summary>TaskDeck について（S-03）。担当: 波1-D、更新の確認の欄は波3-K。</summary>
public partial class AboutPage : UserControl
{
    private readonly AboutPageViewModel _viewModel;

    public AboutPage(AboutPageViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        // 覚えている更新の確認の結果を出す（通信はしない）
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => _viewModel.OpenFolder(_viewModel.DataDirectory);

    private void OnOpenLogs(object sender, RoutedEventArgs e) => _viewModel.OpenFolder(_viewModel.LogDirectory);

    /// <summary>開発時だけの入口（ピッカー3種を並べて確かめる）。</summary>
    private void OnOpenPickers(object sender, RoutedEventArgs e)
    {
        var window = new DevPickerPreviewWindow { Owner = Window.GetWindow(this) };
        window.Show();
    }
}

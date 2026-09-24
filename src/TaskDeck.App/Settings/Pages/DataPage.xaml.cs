using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TaskDeck.App.Services;

namespace TaskDeck.App.Settings.Pages;

/// <summary>
/// データ（S-03、F-101〜F-103・F-127）。担当: 波1-D。
/// ファイル選択と確認ダイアログはここ、実際の読み書きは DataPageViewModel。
/// </summary>
public partial class DataPage : UserControl
{
    private readonly DataPageViewModel _viewModel;
    private readonly ShellService _shell;

    public DataPage(DataPageViewModel viewModel, ShellService shell)
    {
        _viewModel = viewModel;
        _shell = shell;
        InitializeComponent();
        DataContext = viewModel;
        DatabasePathText.Text = viewModel.DatabasePath;
        Loaded += (_, _) => viewModel.Refresh();
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => _viewModel.OpenFolder(_viewModel.DataDirectory);

    private void OnOpenBackupFolder(object sender, RoutedEventArgs e) => _viewModel.OpenFolder(_viewModel.BackupDirectory);

    private void OnBackupNow(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "バックアップの書き出し先",
            FileName = _viewModel.SuggestedBackupName,
            DefaultExt = ".db",
            Filter = "TaskDeck のバックアップ (*.db)|*.db",
            InitialDirectory = DataPageViewModel.SuggestedBackupFolder,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _viewModel.CreateBackup(dialog.FileName);
        }
    }

    /// <summary>復元は戻せない操作なので、ここだけ確認ダイアログを出す（UI 設計書 13.2）。</summary>
    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not BackupEntry entry)
        {
            return;
        }
        var owner = Window.GetWindow(this);
        var answer = MessageBox.Show(
            owner,
            $"{entry.When} のバックアップに戻します。\n\n今のデータはこの内容で置き換わります（置き換える前の状態は backups に退避します）。",
            "バックアップから復元",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK || !_viewModel.Restore(entry))
        {
            return;
        }
        MessageBox.Show(
            owner,
            "復元しました。TaskDeck を終了します。もう一度起動してください。",
            "バックアップから復元",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        _shell.ExitApplication();
    }
}

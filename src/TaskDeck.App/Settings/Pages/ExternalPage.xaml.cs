using System.Windows.Controls;
using System.Windows.Input;
using TaskDeck.App.Input;

namespace TaskDeck.App.Settings.Pages;

/// <summary>外部サービス（S-03）。担当: 波1-D が値の保存まで、波3-K が天気の場所・今すぐ取得・取得の状況。</summary>
public partial class ExternalPage : UserControl
{
    private readonly ExternalPageViewModel _viewModel;

    public ExternalPage(ExternalPageViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        // 開いている間だけ、裏の取得が済んだら状況を読み直す
        Loaded += async (_, _) => await viewModel.AttachAsync();
        Unloaded += (_, _) => viewModel.Detach();
    }

    /// <summary>地名の欄で Enter を押したら探す（IME の変換確定の Enter では探さない）。</summary>
    private void OnPlaceQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (ImeGuard.IsImeEnter(e, (TextBox)sender) || e.Key != Key.Enter)
        {
            return;
        }
        e.Handled = true;
        if (_viewModel.SearchPlacesCommand.CanExecute(null))
        {
            _viewModel.SearchPlacesCommand.Execute(null);
        }
    }
}

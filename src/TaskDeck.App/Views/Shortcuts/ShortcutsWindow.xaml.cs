using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskDeck.App.Input;

namespace TaskDeck.App.Views.Shortcuts;

/// <summary>
/// ショートカット一覧（S-12、?）。ShellService.OpenShortcuts から開く。担当: 波3-J。
/// 閉じるのは Esc（絞り込み中は1回目で絞り込みを消す）・右上の×・ダイアログの外を押したとき（フォーカスが外れたとき）。
/// </summary>
public partial class ShortcutsWindow : Window
{
    private readonly ShortcutsViewModel _viewModel;
    private bool _closing;

    public ShortcutsWindow(ShortcutsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ImeGuard.AddCompositionCompletedHandler(SearchBox, (_, _) => _viewModel.ApplyFilter(SearchBox.Text));
        Loaded += OnLoaded;
        // 画面が低いときは作業領域の高さで止め、本体をスクロールさせる
        MaxHeight = SystemParameters.WorkArea.Height;
        // 絞り込んで行が減っても窓は縮めない（大きさが跳ねないように。フッタは下に残る）。最初に描いた後の高さを下限にする
        ContentRendered += (_, _) => BodyScroll.MinHeight = BodyScroll.ActualHeight;
        // 確認用の起動（ShowActivated=false）ではフォーカスを取らないので、外れたことでは閉じない
        Deactivated += (_, _) =>
        {
            if (ShowActivated)
            {
                SafeClose();
            }
        };
        Closing += (_, _) => _closing = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Owner is null)
        {
            // メイン画面が隠れているとき（オーナーなし）は画面の中央に置く
            var area = SystemParameters.WorkArea;
            Left = area.Left + ((area.Width - ActualWidth) / 2);
            Top = area.Top + ((area.Height - ActualHeight) / 2);
        }
        if (ShowActivated)
        {
            SearchBox.Focus();
        }
    }

    private void SafeClose()
    {
        if (!_closing)
        {
            _closing = true;
            Close();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SafeClose();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ImeGuard.IsComposing(SearchBox))
        {
            _viewModel.ApplyFilter(SearchBox.Text);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ImeGuard.IsComposing(SearchBox))
        {
            return;
        }
        e.Handled = true;
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            return;
        }
        SafeClose();
    }
}

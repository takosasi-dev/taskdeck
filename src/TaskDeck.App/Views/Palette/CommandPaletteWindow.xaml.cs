using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Input;
using TaskDeck.App.ViewModels;

namespace TaskDeck.App.Views.Palette;

/// <summary>
/// コマンドパレット（S-07、Ctrl+K）。ShellService.OpenCommandPalette から開く。担当: 波3-J。
/// ここでは窓の出し入れ（位置・フォーカス・キー・IME）だけを持ち、中身は <see cref="CommandPaletteViewModel"/>。
/// キーボードのフォーカスは入力欄に置いたままにし、↑↓・Enter・Space・Tab・Esc は窓で先に受けて ViewModel に渡す。
/// </summary>
public partial class CommandPaletteWindow : Window
{
    /// <summary>影の余白（PopupShadowStyle の左右10・上8）。</summary>
    private const double ShadowSide = 10;
    private const double ShadowTop = 8;

    private readonly CommandPaletteViewModel _viewModel;
    private readonly ILogger<CommandPaletteWindow> _logger;
    private bool _closing;
    private bool _confirming;
    private bool _syncingText;
    private bool _swallowSpace;

    public CommandPaletteWindow(CommandPaletteViewModel viewModel, ILogger<CommandPaletteWindow> logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _logger = logger;
        DataContext = viewModel;
        Width = (double)FindResource("PaletteWidth") + (ShadowSide * 2);
        viewModel.Confirm = Confirm;
        viewModel.CloseRequested += (_, _) => SafeClose("実行");
        viewModel.TextReplaced += OnTextReplaced;
        viewModel.PropertyChanged += OnViewModelChanged;
        ImeGuard.AddCompositionCompletedHandler(SearchBox, OnSearchCompositionCompleted);

        SourceInitialized += (_, _) => PlaceOverOwner();
        Loaded += OnLoaded;
        Activated += (_, _) => SearchBox.Focus();
        // フォーカスが外れたら閉じる（確認のダイアログを出している間は除く）。
        // 確認用の起動（ShowActivated=false）では、UI Automation の操作で一瞬有効になっては外れるので閉じない
        Deactivated += (_, _) =>
        {
            if (!_confirming && ShowActivated)
            {
                SafeClose("フォーカスが外れた");
            }
        };
        Closing += (_, _) =>
        {
            if (!_closing)
            {
                _logger.LogDebug("パレットを閉じます（窓の外から）");
            }
            _closing = true;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 確認用の起動（ShellService が ShowActivated=false で出す）ではフォーカスを取らない。
        // 取ると裏で窓が有効になり、メイン画面の再描画などで外れた途端に閉じてしまう
        if (ShowActivated)
        {
            SearchBox.Focus();
        }
        await _viewModel.InitializeAsync();
    }

    /// <summary>メイン画面の中央・上から 1/4 に置く（UI 設計書 11.2）。メイン画面が無ければ画面の作業領域に対して同じ位置。</summary>
    private void PlaceOverOwner()
    {
        if (Owner is { IsVisible: true } owner && PresentationSource.FromVisual(owner)?.CompositionTarget is { } target)
        {
            // 最大化中は Left/Top が元の位置のままなので、画面上の実際の位置から求める
            var topLeft = target.TransformFromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
            Left = topLeft.X + ((owner.ActualWidth - Width) / 2);
            Top = topLeft.Y + (owner.ActualHeight / 4) - ShadowTop;
            return;
        }
        var area = SystemParameters.WorkArea;
        Left = area.Left + ((area.Width - Width) / 2);
        Top = area.Top + (area.Height / 4) - ShadowTop;
    }

    private void SafeClose(string reason)
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        _logger.LogDebug("パレットを閉じます（{Reason}）", reason);
        Close();
    }

    private ConfirmChoice Confirm(ConfirmRequest request)
    {
        _confirming = true;
        try
        {
            var answer = MessageBox.Show(this, request.Message, request.Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            return answer switch
            {
                MessageBoxResult.Yes => ConfirmChoice.Yes,
                MessageBoxResult.No => ConfirmChoice.No,
                _ => ConfirmChoice.Cancel,
            };
        }
        finally
        {
            _confirming = false;
        }
    }

    // ---- 入力（IME の変換中は探さない） ----

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingText)
        {
            _viewModel.OnTextChanged(SearchBox.Text, ImeGuard.IsComposing(SearchBox));
        }
    }

    private void OnSearchCompositionCompleted(object sender, RoutedEventArgs e) =>
        _viewModel.OnTextChanged(SearchBox.Text, isComposing: false);

    private void OnTextReplaced(object? sender, string text)
    {
        _syncingText = true;
        SearchBox.Text = text;
        SearchBox.CaretIndex = text.Length;
        _syncingText = false;
    }

    /// <summary>日本語入力のまま Space で完了にしたとき、IME が続けて入れてくる空白を捨てる。</summary>
    private void OnSearchPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_swallowSpace && e.Text is " " or "　")
        {
            e.Handled = true;
        }
        _swallowSpace = false;
    }

    /// <summary>
    /// タスクのチェック（クリック・UI Automation の Toggle のどちらでも来る。Toggle はコマンドを通らないのでイベントで受ける）。
    /// 状態を読み直して値が入っただけのとき（チェックと行の状態が同じ）は何もしない。
    /// </summary>
    private async void OnTaskCheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: PaletteTaskItem item } box && box.IsChecked != item.IsClosed)
        {
            await _viewModel.ToggleTaskAsync(item);
        }
    }

    private async void OnPrefixClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string symbol })
        {
            await _viewModel.InsertPrefixAsync(symbol);
            if (IsActive)
            {
                SearchBox.Focus();
            }
        }
    }

    // ---- キー（UI 設計書 11.5） ----

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _swallowSpace = false;
        if (ImeGuard.IsComposing(SearchBox))
        {
            return;   // 変換中の矢印・Enter・Esc・Space は IME のもの
        }
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                if (!await _viewModel.ClearInputAsync())
                {
                    SafeClose("Esc");
                }
                break;
            case Key.Down:
                e.Handled = true;
                _viewModel.MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                _viewModel.MoveSelection(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                _viewModel.MoveSelection(8);
                break;
            case Key.PageUp:
                e.Handled = true;
                _viewModel.MoveSelection(-8);
                break;
            case Key.Enter when !ImeGuard.IsImeEnter(e, SearchBox):
                e.Handled = true;
                if (ctrl)
                {
                    await _viewModel.CreateFromTextAsync();
                }
                else
                {
                    await _viewModel.ExecuteAsync(null);
                }
                break;
            case Key.Space when !ctrl && _viewModel.CanCompleteWithSpace:
                e.Handled = true;
                _swallowSpace = e.Key == Key.ImeProcessed;
                await _viewModel.ToggleTaskAsync(null);
                break;
            case Key.Tab:
                // フォーカスを入力欄から動かさない（絞り込めるのはプロジェクトとタグを選んでいるときだけ）
                e.Handled = true;
                await _viewModel.NarrowAsync();
                break;
            case Key.Back when SearchBox.Text.Length == 0 && _viewModel.HasScope:
                e.Handled = true;
                await _viewModel.RemoveScopeAsync();
                break;
        }
    }

    /// <summary>選んだ行が見える位置までスクロールする（読み直したときは先頭に戻す）。</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.Rows))
        {
            (ResultList.Template.FindName("PART_Scroll", ResultList) as ScrollViewer)?.ScrollToTop();
            return;
        }
        if (e.PropertyName != nameof(CommandPaletteViewModel.Selected) || _viewModel.Selected is not { } selected)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            (ResultList.ItemContainerGenerator.ContainerFromItem(selected) as FrameworkElement)?.BringIntoView());
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TaskDeck.App.ViewModels;

namespace TaskDeck.App.Views.Focus;

/// <summary>
/// フォーカスモード（S-14、Ctrl+Shift+F）。ShellService.OpenFocusMode から開く。担当: 波3-J。
/// ここではキー（Space・→・Esc・Ctrl+Z）・ボタン・残り時間の時計・窓の移動だけを持ち、中身は <see cref="FocusViewModel"/>。
/// ボタンとチェックはフォーカスを取らない（キーは窓で受ける。Space がボタンを押してしまわないように）。
/// </summary>
public partial class FocusWindow : Window
{
    private readonly FocusViewModel _viewModel;
    private readonly DispatcherTimer _clock;

    public FocusWindow(FocusViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Confirm = Confirm;
        // 残り時間（「あと 12 分」）を進める
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => _viewModel.RefreshClock(), Dispatcher);
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closed += (_, _) => _clock.Stop();
    }

    private ConfirmChoice Confirm(ConfirmRequest request)
    {
        var answer = MessageBox.Show(this, request.Message, request.Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return answer switch
        {
            MessageBoxResult.Yes => ConfirmChoice.Yes,
            MessageBoxResult.No => ConfirmChoice.No,
            _ => ConfirmChoice.Cancel,
        };
    }

    /// <summary>Space 完了して次へ・→ 今はやらない・Esc 終わる（中断位置は残さない F-177）・Ctrl+Z 直前の完了を戻す。押しっぱなしの連打は無視する。</summary>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Space when !ctrl:
                e.Handled = true;
                if (!e.IsRepeat)
                {
                    await _viewModel.CompleteAsync();
                }
                break;
            case Key.Right:
                e.Handled = true;
                if (!e.IsRepeat)
                {
                    await _viewModel.SkipAsync();
                }
                break;
            case Key.Z when ctrl:
                e.Handled = true;
                await _viewModel.UndoAsync();
                break;
        }
    }

    private async void OnCompleteClick(object sender, RoutedEventArgs e) => await _viewModel.CompleteAsync();

    private async void OnSkipClick(object sender, RoutedEventArgs e) => await _viewModel.SkipAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>サブタスクのチェック（クリック・UI Automation の Toggle のどちらでも来る）。状態と同じ値が入っただけのときは何もしない。</summary>
    private async void OnSubtaskCheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: FocusSubtask subtask } box && box.IsChecked != subtask.IsDone)
        {
            await _viewModel.ToggleSubtaskAsync(subtask);
        }
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}

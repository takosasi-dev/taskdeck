using System.Windows;
using System.Windows.Input;
using TaskDeck.App.Input;
using Wpf.Ui.Controls;

namespace TaskDeck.App.Views.Scratch;

/// <summary>
/// 使い捨てリストの小窓（担当: 波3-N）。出すのは ScratchPresenter（ShellService.ShowToolWindow 経由。Show を直接呼ばない）。
/// ここでは位置と大きさ・名前の欄・▾ のポップアップだけを受け持ち、中身は <see cref="ScratchWindowViewModel"/>。
/// </summary>
public partial class ScratchWindow : FluentWindow
{
    public ScratchWindow(ScratchWindowViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Closing += (_, _) => RememberPlacement();
        Closed += (_, _) => viewModel.Release();
        // 開いたときにすぐ打てるように（前面に出ていないとき＝確認用の起動では何もしない）
        ContentRendered += (_, _) => ListContent.FocusAddBox();
    }

    public ScratchWindowViewModel ViewModel { get; }

    /// <summary>前回の位置と大きさに戻す（画面の外に出ていたら既定の位置＝画面の中央に出す）。表示する前に呼ぶ。</summary>
    public void ApplyPlacement(ScratchWindowState state)
    {
        Width = Math.Max(state.Width, MinWidth);
        Height = Math.Max(state.Height, MinHeight);
        if (state.Left is { } left && state.Top is { } top && IsOnScreen(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    /// <summary>タイトルバーを掴める範囲（横 80px・縦 40px）が画面（マルチモニタを含む）に入っているか。</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var minX = SystemParameters.VirtualScreenLeft;
        var minY = SystemParameters.VirtualScreenTop;
        var maxX = minX + SystemParameters.VirtualScreenWidth;
        var maxY = minY + SystemParameters.VirtualScreenHeight;
        return left + 80 < maxX && top + 40 < maxY && left + width - 80 > minX && top > minY - 8;
    }

    private void RememberPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            ViewModel.RememberPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }

    private void OnSwitchClick(object sender, RoutedEventArgs e) => SwitchPopup.IsOpen = true;

    private void OnChoiceClick(object sender, RoutedEventArgs e) => SwitchPopup.IsOpen = false;

    private void OnNameLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ViewModel.List?.CommitName();

    /// <summary>名前の欄で Enter（変換確定の Enter は除く）: 名前を確定して項目の入力へ移る。</summary>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box || e.Key != Key.Enter || ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        e.Handled = true;
        ViewModel.List?.CommitName();
        ListContent.FocusAddBox();
    }
}

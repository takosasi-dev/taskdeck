using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskDeck.App.Residency;

namespace TaskDeck.App.Settings.Pages;

/// <summary>
/// ショートカット（S-03、F-123）。担当: 波3-H（波1-D は表示のみ）。
/// 「変更」の後に押された組み合わせを "Ctrl+Alt+K" の形にして ViewModel に渡す（判定と保存は ViewModel）。
/// 入力の間は、押されたキーをほかの操作（ボタンの Space・Tab の移動など）に回さない。Esc でやめる。
/// </summary>
public partial class ShortcutsPage : UserControl
{
    private readonly ShortcutsPageViewModel _viewModel;

    public ShortcutsPage(ShortcutsPageViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Attach();
        Unloaded += (_, _) => viewModel.Detach();
        PreviewKeyDown += OnPreviewKeyDown;
        // 入力の途中で他のアプリへ移った（Alt+Tab など）ら、入力をやめてホットキーを戻す（外したままにしない）
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                viewModel.CancelRecording();
            }
        };
    }

    private void OnChangeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotkeyRow row)
        {
            _viewModel.StartRecording(row);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.Recording is null)
        {
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            e.Handled = true; // 修飾キーだけのうちは待つ
            return;
        }
        e.Handled = true;
        if (key == Key.Escape)
        {
            _viewModel.CancelRecording();
            return;
        }
        var gesture = HotkeyGesture.FromKey((uint)Keyboard.Modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        _viewModel.Assign(gesture?.ToString());
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Input;
using TaskDeck.App.Residency;
using TaskDeck.App.Services;

namespace TaskDeck.App.Views.QuickInput;

/// <summary>
/// クイック入力（S-02、Ctrl+Shift+Space）。ShellService.OpenQuickInput から開く。担当: 波3-H。
/// 窓は1つを使い回す（閉じるのではなく隠す）。出すたびにマウスカーソルのあるモニタの中央・上から1/3へ動かし、
/// 150ms で不透明度 0→1 と上へ 8px のスライド（アニメーションを減らす設定なら動かさない。UI 設計書 7章）。
/// Esc・登録・フォーカスが外れたら隠す（本体は常駐のまま）。確認用の起動（TASKDECK_DEV_NOACTIVATE=1）では自分でフォーカスを取らない。
/// </summary>
public partial class QuickInputWindow : Window
{
    /// <summary>ホットキーが押された時刻（Stopwatch）。入力欄にフォーカスが入ったら、そこまでの時間をログに出す（NFR 3.2 の 150ms）。</summary>
    private static long _requestedAt;

    private readonly QuickInputViewModel _viewModel;
    private readonly ThemeService _theme;
    private readonly ResidencySwitches _switches;
    private readonly ILogger<QuickInputWindow> _logger;
    private bool _submitting;
    private bool _deactivatedWithPopup;

    public QuickInputWindow(QuickInputViewModel viewModel, ThemeService theme, ResidencySwitches switches, ILogger<QuickInputWindow> logger)
    {
        _viewModel = viewModel;
        _theme = theme;
        _switches = switches;
        _logger = logger;
        InitializeComponent();
        DataContext = viewModel;
        // --tray の起動ではメイン画面より先にこの窓ができ、WPF が最初の窓として Application.MainWindow に入れてしまう。
        // そのままだと WPF-UI がテーマの切り替えで MainWindow の地を塗り、透明な縁が塗られるので外しておく
        if (Application.Current?.MainWindow == this)
        {
            Application.Current.MainWindow = null;
        }

        IsVisibleChanged += OnIsVisibleChanged;
        Activated += (_, _) => FocusInput();
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnWindowKeyDown;
        ImeGuard.AddCompositionCompletedHandler(InputBox, (_, _) => _viewModel.Refresh());
        InputBox.GotKeyboardFocus += (_, _) => LogShownTime();

        DatePicker.Picked += (_, e) => Pick(DuePopup, () => _viewModel.CorrectDue(e.Date, e.Time));
        DatePicker.Cancelled += (_, _) => DuePopup.IsOpen = false;
        PriorityPicker.Picked += (_, e) => Pick(PriorityPopup, () => _viewModel.CorrectPriority(e.Priority));
        PriorityPicker.Cancelled += (_, _) => PriorityPopup.IsOpen = false;
        ProjectPicker.Picked += async (_, e) =>
        {
            ProjectPopup.IsOpen = false;
            await _viewModel.CorrectProjectAsync(e.ProjectId);
        };
        ProjectPicker.Cancelled += (_, _) => ProjectPopup.IsOpen = false;
    }

    /// <summary>ホットキーやトレイから開く直前に呼ぶ（表示までの時間を測る起点）。</summary>
    public static void MarkRequested() => Interlocked.Exchange(ref _requestedAt, Stopwatch.GetTimestamp());

    /// <summary>起動が落ち着いたときに窓のハンドルを先に作っておく（最初のホットキーでも速く出すため）。</summary>
    public void Prepare() => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>入力窓を引っ込める（本体は常駐のまま）。登録の途中は隠さない（失敗したときに入力が見えなくなるため）。</summary>
    public void CloseInput()
    {
        if (_submitting)
        {
            return;
        }
        _deactivatedWithPopup = false;
        DuePopup.IsOpen = false;
        PriorityPopup.IsOpen = false;
        ProjectPopup.IsOpen = false;
        Hide();
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
    }

    /// <summary>× や Alt+F4 でも閉じずに隠す（窓は使い回す。アプリの終了時は WPF が取り消しを無視して閉じる）。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        CloseInput();
        base.OnClosing(e);
    }

    private bool AnyPopupOpen => DuePopup.IsOpen || PriorityPopup.IsOpen || ProjectPopup.IsOpen;

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }
        PlaceOnCursorMonitor();
        PlayShowAnimation();
        await _viewModel.OpenAsync();
    }

    /// <summary>マウスカーソルのあるモニタの中央、上から 1/3 に置く（UI 設計書 5.2・NFR のマルチモニタ）。</summary>
    private void PlaceOnCursorMonitor()
    {
        if (NativeMethods.CursorMonitor() is not { } monitor)
        {
            _logger.LogWarning("マウスカーソルのあるモニタを取れませんでした。前の位置に出します");
            return;
        }
        var (work, scale) = monitor;
        var width = (int)Math.Round((ActualWidth > 0 ? ActualWidth : 680) * scale);
        var x = work.Left + ((work.Width - width) / 2);
        var y = work.Top + (work.Height / 3) - (int)Math.Round(40 * scale);
        if (!NativeMethods.MoveWindow(new WindowInteropHelper(this).Handle, x, y))
        {
            _logger.LogWarning("クイック入力窓を動かせませんでした（エラー {Error}）", System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        }
    }

    private void PlayShowAnimation()
    {
        Root.BeginAnimation(OpacityProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        if (_theme.ReduceMotion)
        {
            Root.Opacity = 1;
            Slide.Y = 0;
            return;
        }
        var duration = (Duration)FindResource("DurationQuickInput");
        var ease = (IEasingFunction)FindResource("EaseDecelerate");
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = ease });
    }

    private void FocusInput()
    {
        if (_switches.NoActivate)
        {
            return; // 確認用の起動では前面にもフォーカスにもしない（依頼者のキーを横取りしない）
        }
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void LogShownTime()
    {
        var requested = Interlocked.Exchange(ref _requestedAt, 0);
        if (requested != 0)
        {
            _logger.LogInformation("クイック入力の表示 {Milliseconds} ms（ホットキー → 入力欄にフォーカス）", (int)Stopwatch.GetElapsedTime(requested).TotalMilliseconds);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_switches.NoActivate)
        {
            return; // 確認用の起動では、たまたま触られても閉じない（UI Automation で入力している途中に消えないように）
        }
        if (AnyPopupOpen)
        {
            _deactivatedWithPopup = true; // ピッカーを閉じたときに、まだ外にいれば閉じる
            return;
        }
        CloseInput();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (_deactivatedWithPopup && !IsActive)
        {
            CloseInput();
            return;
        }
        _deactivatedWithPopup = false;
        FocusInput();
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // IME の変換中はパースしない。確定したら CompositionCompleted で走る（CLAUDE.md 1.7）
        if (!ImeGuard.IsComposing(InputBox))
        {
            _viewModel.Refresh();
        }
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (ImeGuard.IsImeEnter(e, InputBox) || ImeGuard.IsComposing(InputBox))
        {
            return; // 変換確定の Enter・候補を選ぶ ↑↓ は IME に任せる
        }
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await SubmitAsync();
                break;
            case Key.Up when _viewModel.RecallOlder():
            case Key.Down when _viewModel.RecallNewer():
                e.Handled = true;
                InputBox.CaretIndex = InputBox.Text.Length;
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control && InputBox.Text.Length == 0:
                e.Handled = true;
                await _viewModel.UndoLastAsync();
                break;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // ピッカーを開いている間の Esc はピッカーが受ける（ピッカーだけ閉じる）。変換中の Esc は IME が変換をやめるのに使う
        if (e.Key == Key.Escape && !AnyPopupOpen && !ImeGuard.IsComposing(InputBox))
        {
            e.Handled = true;
            CloseInput();
        }
    }

    private async Task SubmitAsync()
    {
        // 変換中は未確定の読みがそのまま入っているので登録しない（「Enter 登録」ボタンから来たときも）
        if (_submitting || ImeGuard.IsComposing(InputBox))
        {
            return;
        }
        _submitting = true;
        bool added;
        try
        {
            added = await _viewModel.SubmitAsync();
        }
        finally
        {
            _submitting = false;
        }
        if (added)
        {
            CloseInput();
        }
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs e) => await SubmitAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseInput();

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.RecallOlder())
        {
            FocusInput();
        }
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickInputChip chip)
        {
            return;
        }
        switch (chip.Kind)
        {
            case QuickInputChipKind.Due:
                (DatePicker.Date, DatePicker.Time) = _viewModel.CurrentDue();
                Open(DuePopup, sender);
                break;
            case QuickInputChipKind.Priority:
                PriorityPicker.Priority = _viewModel.CurrentPriority();
                Open(PriorityPopup, sender);
                break;
            case QuickInputChipKind.Project:
                ProjectPicker.ProjectId = _viewModel.CurrentProjectId();
                Open(ProjectPopup, sender);
                break;
        }
    }

    private void Open(Popup popup, object target)
    {
        DuePopup.IsOpen = false;
        PriorityPopup.IsOpen = false;
        ProjectPopup.IsOpen = false;
        popup.PlacementTarget = target as UIElement;
        popup.IsOpen = true;
    }

    private static void Pick(Popup popup, Action apply)
    {
        popup.IsOpen = false;
        apply();
    }
}

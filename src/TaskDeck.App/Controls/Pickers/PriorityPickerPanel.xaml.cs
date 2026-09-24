using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskDeck.Core;

namespace TaskDeck.App.Controls.Pickers;

/// <summary>
/// 優先度ピッカー（S-09、UI 設計書 12.3）。担当: 波1-D。
/// 公開 API（変えない）: Priority（現在値）、Picked、Cancelled。
/// キーボード: 開くと現在の優先度にフォーカス。↑↓ で移動、Enter / Space で決定、1〜4 と 0 で直接指定、Esc で取り消し。
/// </summary>
public partial class PriorityPickerPanel : UserControl
{
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.Register(
        nameof(Priority), typeof(Priority), typeof(PriorityPickerPanel),
        new PropertyMetadata(Priority.None, (d, _) => ((PriorityPickerPanel)d).MarkCurrent()));

    public PriorityPickerPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        // 開いたときに現在の優先度へフォーカスを置く（↑↓ がそこから始まる）
        Loaded += (_, _) =>
        {
            MarkCurrent();
            FocusCurrent();
        };
    }

    public Priority Priority
    {
        get => (Priority)GetValue(PriorityProperty);
        set => SetValue(PriorityProperty, value);
    }

    public event EventHandler<PriorityPickedEventArgs>? Picked;

    public event EventHandler? Cancelled;

    private IEnumerable<Button> RowButtons => Rows.Children.OfType<Button>();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        // 数字キーで直接指定（Ctrl の有無は問わない。ホスト側が Ctrl+1〜4 を割り当てている）
        var digit = e.Key switch
        {
            Key.D0 or Key.NumPad0 => 0,
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            _ => -1,
        };
        if (digit >= 0)
        {
            Pick((Priority)digit);
            e.Handled = true;
        }
    }

    /// <summary>現在の行を目立たせる（地を Surface.Hover、文字を Medium。モックアップ Pickers.dc.html）。</summary>
    private void MarkCurrent()
    {
        foreach (var button in RowButtons)
        {
            if (ValueOf(button) == Priority)
            {
                button.SetResourceReference(BackgroundProperty, "SurfaceHoverBrush");
                button.FontWeight = FontWeights.Medium;
            }
            else
            {
                button.ClearValue(BackgroundProperty);
                button.ClearValue(FontWeightProperty);
            }
        }
    }

    private void FocusCurrent() =>
        (RowButtons.FirstOrDefault(b => ValueOf(b) == Priority) ?? RowButtons.FirstOrDefault())?.Focus();

    private static Priority ValueOf(Button button) =>
        (Priority)int.Parse((string)button.Tag, CultureInfo.InvariantCulture);

    private void OnPick(object sender, RoutedEventArgs e) => Pick(ValueOf((Button)sender));

    private void Pick(Priority value) => Picked?.Invoke(this, new PriorityPickedEventArgs(value));
}

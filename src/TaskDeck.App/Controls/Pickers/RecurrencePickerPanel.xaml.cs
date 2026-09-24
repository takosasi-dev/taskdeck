using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskDeck.App.Input;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Controls.Pickers;

/// <summary>
/// 繰り返しピッカー（S-09、UI 設計書 12.2）。担当: 波2-F。
/// 公開 API（変えない）: Recurrence（現在値）、BaseDueAt / DueHasTime（プレビューの起点）、Picked、Cancelled。
/// 中身の組み立て・判定は <see cref="RecurrencePickerModel"/>。開くたび（Loaded のたび）に現在値を写し直す。
/// 影と枠はパネルが持つので、ホストは Popup の直下に置く（INTERFACES 5.3）。
/// キー: ↑↓ でプリセット、Tab で次の欄、Enter で決定、Esc で Cancelled。
/// </summary>
public partial class RecurrencePickerPanel : UserControl
{
    public static readonly DependencyProperty RecurrenceProperty = DependencyProperty.Register(
        nameof(Recurrence), typeof(RecurrenceInput), typeof(RecurrencePickerPanel), new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty BaseDueAtProperty = DependencyProperty.Register(
        nameof(BaseDueAt), typeof(DateTime?), typeof(RecurrencePickerPanel), new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty DueHasTimeProperty = DependencyProperty.Register(
        nameof(DueHasTime), typeof(bool), typeof(RecurrencePickerPanel), new PropertyMetadata(false, OnInputChanged));

    private readonly RecurrencePickerModel? _model;

    public RecurrencePickerPanel()
    {
        InitializeComponent();
        if (AppServices.IsReady)
        {
            _model = new RecurrencePickerModel(
                AppServices.Get<IRecurrenceEngine>(),
                AppServices.Get<IClock>(),
                AppServices.Get<ISettingsStore>().Current.Calendar.WeekStartsOnMonday);
            Root.DataContext = _model;
        }
        PreviewKeyDown += OnPanelPreviewKeyDown;
        // Popup は開くたびに中身を読み込み直す（Loaded が毎回来る）。そのたびに現在値を写し直し、自分でフォーカスを取る（INTERFACES 5.3）
        Loaded += (_, _) =>
        {
            Reload();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusChoice);
        };
    }

    /// <summary>現在の繰り返し。null はなし。</summary>
    public RecurrenceInput? Recurrence
    {
        get => (RecurrenceInput?)GetValue(RecurrenceProperty);
        set => SetValue(RecurrenceProperty, value);
    }

    /// <summary>プレビューの起点（タスクの期限、UTC）。null なら今日から。</summary>
    public DateTime? BaseDueAt
    {
        get => (DateTime?)GetValue(BaseDueAtProperty);
        set => SetValue(BaseDueAtProperty, value);
    }

    public bool DueHasTime
    {
        get => (bool)GetValue(DueHasTimeProperty);
        set => SetValue(DueHasTimeProperty, value);
    }

    public event EventHandler<RecurrencePickedEventArgs>? Picked;

    public event EventHandler? Cancelled;

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RecurrencePickerPanel)d).Reload();

    private void Reload() => _model?.Load(Recurrence, BaseDueAt, DueHasTime);

    private void OnCommit(object sender, RoutedEventArgs e) => Commit();

    /// <summary>「繰り返しをやめる」（F-036。作成済みのタスクは残る）。</summary>
    private void OnStop(object sender, RoutedEventArgs e) => Picked?.Invoke(this, new RecurrencePickedEventArgs(null));

    private void Commit()
    {
        if (_model is not { CanCommit: true })
        {
            return;
        }
        Picked?.Invoke(this, new RecurrencePickedEventArgs(_model.Build()));
    }

    /// <summary>
    /// Esc で閉じる、Enter で決定。開いているドロップダウン（コンボボックス・日付）の中ではそちらに任せる。
    /// Enter はラジオや曜日の上でも決定にする（ボタンの上ならボタン自身、日付欄なら日付の読み取りに任せる）。
    /// </summary>
    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInOpenDropDown(source))
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !ImeGuard.IsImeEnter(e, source) && source is not Button && FindAncestor<DatePicker>(source) is null)
        {
            Commit();
            e.Handled = true;
        }
    }

    private void OnChoiceKeyDown(object sender, KeyEventArgs e)
    {
        if (_model is null || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }
        _model.MoveChoice(e.Key == Key.Down ? 1 : -1);
        FocusChoice();
        e.Handled = true;
    }

    private void OnMonthlyModeKeyDown(object sender, KeyEventArgs e)
    {
        if (_model is null || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }
        _model.ToggleMonthlyMode();
        (_model.MonthlyMode == MonthlyMode.DayOfMonth ? MonthByDayRadio : MonthByNthRadio).Focus();
        e.Handled = true;
    }

    private void OnBaseKindKeyDown(object sender, KeyEventArgs e)
    {
        if (_model is null || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return;
        }
        _model.ToggleBaseKind();
        (_model.BaseKind == RecurrenceBaseKind.DueDate ? BaseDueRadio : BaseCompletedRadio).Focus();
        e.Handled = true;
    }

    /// <summary>選んでいるプリセットにフォーカスを置く（開いたとき・矢印で選び直したとき）。</summary>
    private void FocusChoice()
    {
        if (_model is null)
        {
            return;
        }
        var radio = ChoiceList.Children.OfType<RadioButton>().FirstOrDefault(r => Equals(r.Tag, _model.Choice));
        radio?.Focus();
    }

    private static bool IsInOpenDropDown(DependencyObject source) =>
        FindAncestor<ComboBox>(source) is { IsDropDownOpen: true } || FindAncestor<DatePicker>(source) is { IsDropDownOpen: true };

    /// <summary>見た目の親、無ければ論理の親（ドロップダウンの中身は Popup を経て持ち主へ戻る）をたどる。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T found)
            {
                return found;
            }
            node = (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}

/// <summary>
/// 列挙値が ConverterParameter（名前。Visibility ではカンマ区切りで複数可）と一致するか。
/// ラジオの IsChecked（双方向）と、選んだ項目だけ出す Visibility に使う。
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var match = value is not null
            && parameter is string names
            && names.Split(',').Any(n => string.Equals(n.Trim(), value.ToString(), StringComparison.Ordinal));
        return targetType == typeof(Visibility) ? (match ? Visibility.Visible : Visibility.Collapsed) : match;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

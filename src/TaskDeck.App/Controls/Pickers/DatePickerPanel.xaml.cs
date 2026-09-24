using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TaskDeck.App.Input;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Controls.Pickers;

/// <summary>月カレンダーの曜日の見出し1つ。</summary>
public sealed record WeekdayHeader(DayOfWeek Day, string Name)
{
    /// <summary>一覧の項目としての読み上げ名（UI Automation は項目の ToString を使う）。</summary>
    public override string ToString() => Name;
}

/// <summary>月カレンダーの日のセル1つ（表示用）。IsTabStop は Tab でカレンダーに入ったときに止まる1つ（選択中の日→今日→月初）。</summary>
public sealed record CalendarDay(DateOnly Date, bool IsOtherMonth, bool IsToday, bool IsSelected, string? HolidayName, bool IsTabStop = false)
{
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    /// <summary>一覧の項目としての読み上げ名（UI Automation は項目の ToString を使う）。</summary>
    public override string ToString() => AccessibleName;

    public string Text => Date.Day.ToString(CultureInfo.InvariantCulture);

    public DayOfWeek DayOfWeek => Date.DayOfWeek;

    public bool IsHoliday => HolidayName is not null;

    /// <summary>読み上げ用（「9月23日 水曜日、今日、秋分の日」）。</summary>
    public string AccessibleName =>
        Date.ToString("M月d日 dddd", Japanese)
        + (IsToday ? "、今日" : "")
        + (IsSelected ? "、選択中" : "")
        + (HolidayName is { } name ? "、" + name : "");
}

/// <summary>
/// 日付ピッカー（S-09、UI 設計書 12.1）。担当: 波1-D。
/// 公開 API（変えない）: Date / Time（開く前にホストが入れる現在値）、Picked、Cancelled。
/// キーボード: 開くと「今日」の行にフォーカス。↑↓ でクイック選択、Tab でカレンダーへ（矢印で日、PageUp/PageDown で月、
/// Home/End で月初・月末）、Enter / Space で決定、Esc で取り消し。時刻欄は Enter でその時刻に決める。
/// </summary>
public partial class DatePickerPanel : UserControl
{
    public static readonly DependencyProperty DateProperty = DependencyProperty.Register(
        nameof(Date), typeof(DateOnly?), typeof(DatePickerPanel),
        new PropertyMetadata(null, (d, _) => ((DatePickerPanel)d).Refresh()));

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(TimeOnly?), typeof(DatePickerPanel),
        new PropertyMetadata(null, (d, _) => ((DatePickerPanel)d).RefreshTime()));

    private List<CalendarDay> _days = [];
    private DateOnly _month;

    public DatePickerPanel()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
        // Popup は開くたびに中身を読み込み直す（Loaded が毎回来る）。そのたびに今日と現在値から作り直す
        Loaded += (_, _) =>
        {
            Refresh();
            QuickRows.Children.OfType<Button>().FirstOrDefault()?.Focus();
        };
    }

    /// <summary>現在の期限（ローカル日付）。null は期限なし。</summary>
    public DateOnly? Date
    {
        get => (DateOnly?)GetValue(DateProperty);
        set => SetValue(DateProperty, value);
    }

    /// <summary>現在の時刻。null は終日。</summary>
    public TimeOnly? Time
    {
        get => (TimeOnly?)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public event EventHandler<DatePickedEventArgs>? Picked;

    public event EventHandler? Cancelled;

    private DateOnly Today => AppServices.IsReady ? AppServices.Get<IClock>().LocalToday() : new DateOnly(2026, 1, 1);

    /// <summary>現在値と今日の日付を画面に反映する（開くたびにホストが値を入れるので、その都度呼ばれる）。</summary>
    private void Refresh()
    {
        if (!IsLoaded)
        {
            return;
        }
        var today = Today;
        TodayLabel.Text = QuickDates.Label(today);
        TomorrowLabel.Text = QuickDates.Label(today.AddDays(1));
        WeekendLabel.Text = QuickDates.Label(QuickDates.ThisWeekend(today));
        NextWeekLabel.Text = QuickDates.Label(QuickDates.NextWeek(today));
        NextMonthLabel.Text = QuickDates.Label(QuickDates.NextMonth(today));
        BusinessDayLabel.Text = AppServices.IsReady
            ? QuickDates.Label(QuickDates.NextBusinessDay(today, AppServices.Get<IHolidayProvider>()))
            : "";

        _month = Date ?? today;
        BuildMonth();
        RefreshTime();
    }

    /// <summary>時刻欄（時刻があるときだけ出す）。表示中の月はそのまま。</summary>
    private void RefreshTime()
    {
        if (!IsLoaded)
        {
            return;
        }
        var hasTime = Time is not null;
        TimeRow.Visibility = hasTime ? Visibility.Visible : Visibility.Collapsed;
        AddTimeLabel.Text = hasTime ? "時刻を消す" : "時刻を追加";
        TimeBox.Text = Time?.ToString("HH\\:mm", CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>表示中の月（_month）のセルを作り直す。週の開始曜日は設定（F-125）に従う。</summary>
    private void BuildMonth()
    {
        var firstDay = AppServices.IsReady && AppServices.Get<ISettingsStore>().Current.Calendar.WeekStartsOnMonday
            ? DayOfWeek.Monday
            : DayOfWeek.Sunday;
        IHolidayProvider? holidays = AppServices.IsReady ? AppServices.Get<IHolidayProvider>() : null;
        var today = Today;
        var selected = Date;

        MonthLabel.Text = QuickDates.MonthTitle(_month);
        WeekdayRow.ItemsSource = QuickDates.WeekDays(firstDay)
            .Select(d => new WeekdayHeader(d, QuickDates.DayOfWeekName(d)))
            .ToList();
        var grid = QuickDates.MonthGrid(_month, firstDay);
        var inMonth = (DateOnly d) => d.Year == _month.Year && d.Month == _month.Month;
        var tabStop = selected is { } s && inMonth(s) ? s
            : inMonth(today) ? today
            : new DateOnly(_month.Year, _month.Month, 1);
        _days = [.. grid.Select(d => new CalendarDay(
            d,
            IsOtherMonth: !inMonth(d),
            IsToday: d == today,
            IsSelected: d == selected,
            HolidayName: holidays?.GetName(d),
            IsTabStop: d == tabStop))];
        DaysList.ItemsSource = _days;
    }

    private void OnToday(object sender, RoutedEventArgs e) => Pick(Today);

    private void OnTomorrow(object sender, RoutedEventArgs e) => Pick(Today.AddDays(1));

    private void OnWeekend(object sender, RoutedEventArgs e) => Pick(QuickDates.ThisWeekend(Today));

    private void OnNextWeek(object sender, RoutedEventArgs e) => Pick(QuickDates.NextWeek(Today));

    private void OnNextMonth(object sender, RoutedEventArgs e) => Pick(QuickDates.NextMonth(Today));

    private void OnNextBusinessDay(object sender, RoutedEventArgs e) =>
        Pick(QuickDates.NextBusinessDay(Today, AppServices.Get<IHolidayProvider>()));

    private void OnPreviousMonth(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(-1));

    private void OnNextMonthPage(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(1));

    private void ShowMonth(DateOnly month)
    {
        _month = month;
        BuildMonth();
    }

    private void OnDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateOnly date })
        {
            Pick(date);
        }
    }

    /// <summary>カレンダーの中のキー操作。矢印で決定しない（動かすだけ。決めるのは Enter / Space / クリック）。</summary>
    private void OnDaysKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not Button { Tag: DateOnly current })
        {
            return;
        }
        DateOnly? target = e.Key switch
        {
            Key.Left => current.AddDays(-1),
            Key.Right => current.AddDays(1),
            Key.Up => current.AddDays(-7),
            Key.Down => current.AddDays(7),
            Key.PageUp => current.AddMonths(-1),
            Key.PageDown => current.AddMonths(1),
            Key.Home => new DateOnly(current.Year, current.Month, 1),
            Key.End => new DateOnly(current.Year, current.Month, 1).AddMonths(1).AddDays(-1),
            _ => null,
        };
        if (target is not { } date)
        {
            return;
        }
        e.Handled = true;
        if (date.Year != _month.Year || date.Month != _month.Month)
        {
            ShowMonth(date);
        }
        FocusDay(date);
    }

    private void FocusDay(DateOnly date)
    {
        DaysList.UpdateLayout();
        var index = _days.FindIndex(d => d.Date == date);
        if (index >= 0
            && DaysList.ItemContainerGenerator.ContainerFromIndex(index) is ContentPresenter presenter
            && VisualTreeHelper.GetChildrenCount(presenter) > 0
            && VisualTreeHelper.GetChild(presenter, 0) is Button button)
        {
            button.Focus();
        }
    }

    /// <summary>「時刻を追加」。押して初めて時刻欄が出る（終日のタスクの方が多いため）。もう一度押すと終日に戻す。</summary>
    private void OnAddTime(object sender, RoutedEventArgs e)
    {
        if (TimeRow.Visibility == Visibility.Visible)
        {
            Time = null;
            TimeRow.Visibility = Visibility.Collapsed;
            AddTimeLabel.Text = "時刻を追加";
            return;
        }
        TimeRow.Visibility = Visibility.Visible;
        AddTimeLabel.Text = "時刻を消す";
        if (TimeBox.Text.Length == 0)
        {
            var defaultTime = AppServices.IsReady
                ? AppServices.Get<ISettingsStore>().Current.Notifications.DefaultReminderTime
                : new TimeOnly(9, 0);
            TimeBox.Text = defaultTime.ToString("HH\\:mm", CultureInfo.InvariantCulture);
        }
        TimeBox.Focus();
        TimeBox.SelectAll();
    }

    private void OnTimeKeyDown(object sender, KeyEventArgs e)
    {
        if (ImeGuard.IsImeEnter(e, TimeBox) || e.Key != Key.Enter)
        {
            return;
        }
        // Enter で「この時刻で決定」。日付は選択中（無ければ今日）
        if (TryReadTime(out var time))
        {
            Time = time;
            Pick(Date ?? Today);
        }
        e.Handled = true;
    }

    private void OnTimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (TryReadTime(out var time))
        {
            Time = time;
        }
    }

    /// <summary>入力欄の時刻を読む。全角で打たれても半角に寄せる。読めなければ false（前の値のまま）。</summary>
    private bool TryReadTime(out TimeOnly time)
    {
        var text = TextNormalizer.ForName(TimeBox.Text);
        if (TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out time))
        {
            TimeHint.Text = "HH:mm";
            TimeHint.SetResourceReference(ForegroundProperty, "TextTertiaryBrush");
            return true;
        }
        if (text.Length > 0)
        {
            TimeHint.Text = "時刻の形が違います";
            TimeHint.SetResourceReference(ForegroundProperty, "StateOverdueBrush");
        }
        return false;
    }

    private void OnClear(object sender, RoutedEventArgs e) => Picked?.Invoke(this, new DatePickedEventArgs(null, null));

    private void Pick(DateOnly date)
    {
        var time = TimeRow.Visibility == Visibility.Visible && TryReadTime(out var parsed) ? parsed : null as TimeOnly?;
        Picked?.Invoke(this, new DatePickedEventArgs(date, time));
    }
}

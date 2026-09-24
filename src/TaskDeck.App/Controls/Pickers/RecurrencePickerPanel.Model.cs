using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Controls.Pickers;

/// <summary>繰り返しピッカーのラジオ（UI 設計書 12.2）。並び順がそのまま矢印キーの順になる。</summary>
public enum RecurrenceChoice
{
    None,
    Daily,
    Weekdays,
    Weekly,
    Monthly,
    Yearly,
    Custom,
}

/// <summary>「毎月」の決め方: 日付（22日・月末）か、第N曜日か。</summary>
public enum MonthlyMode
{
    DayOfMonth,
    NthWeekday,
}

/// <summary>カスタムの単位（N日ごと・N週ごと・Nか月ごと）。</summary>
public enum IntervalUnit
{
    Day,
    Week,
    Month,
}

/// <summary>コンボボックスの1項目（値と表示）。ToString は表示名（読み上げ・UI Automation の名前になる）。</summary>
public sealed record PickerOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>曜日のトグル1つ。</summary>
public sealed partial class WeekdayToggle(DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Label { get; } = DateLabels.WeekdayName(day).ToString();

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 繰り返しピッカーの中身（WPF に依存しない）。
/// RRULE の組み立ては <see cref="RecurrencePresets"/>、次回の計算は <see cref="IRecurrenceEngine"/>、説明文は <see cref="RecurrenceText"/> に任せる。
/// 既存の RRULE を画面に写すときは、読み取った状態から RecurrencePresets で組み直し、元の文字列と一致したときだけ採用する。
/// 一致しない（この画面で表せない）RRULE は「カスタム」に置いたまま、手を付けなければそのまま返す。
/// </summary>
public sealed partial class RecurrencePickerModel : ObservableObject
{
    public const int MaxInterval = 999;

    private static readonly RecurrenceChoice[] ChoiceOrder = Enum.GetValues<RecurrenceChoice>();

    /// <summary>変えると結果が変わる入力。変わったら検証とプレビューをやり直す。</summary>
    private static readonly HashSet<string> Inputs =
    [
        nameof(Choice), nameof(MonthlyMode), nameof(MonthDay), nameof(MonthNth), nameof(MonthWeekday),
        nameof(IntervalText), nameof(Unit), nameof(BaseKind), nameof(EndKind), nameof(EndDate), nameof(EndCountText),
    ];

    /// <summary>規則そのもの（RRULE）を変える入力。変わったら「読めない形」の元の RRULE は捨てる。</summary>
    private static readonly HashSet<string> RuleInputs =
    [
        nameof(Choice), nameof(MonthlyMode), nameof(MonthDay), nameof(MonthNth), nameof(MonthWeekday), nameof(IntervalText), nameof(Unit),
    ];

    /// <summary>入力から計算して出すもの。</summary>
    private static readonly string[] Derived =
    [
        nameof(IsRaw), nameof(RawRuleText), nameof(HasRule), nameof(ShowsCustomWeekdays), nameof(WeekdayHint),
        nameof(MonthDayHint), nameof(YearlyHint), nameof(IsEndDate), nameof(IsEndCount), nameof(CanCommit), nameof(HasPreviewDates),
    ];

    private readonly IRecurrenceEngine _engine;
    private readonly IClock _clock;
    private DateTime _baseDueUtc;
    private bool _dueHasTime;
    private bool _loading;

    [ObservableProperty]
    private RecurrenceChoice _choice;

    [ObservableProperty]
    private MonthlyMode _monthlyMode;

    /// <summary>1〜31、-1 は月末。</summary>
    [ObservableProperty]
    private int _monthDay = 1;

    /// <summary>1〜5、-1 は最終。</summary>
    [ObservableProperty]
    private int _monthNth = 1;

    [ObservableProperty]
    private DayOfWeek _monthWeekday;

    [ObservableProperty]
    private string _intervalText = "2";

    [ObservableProperty]
    private IntervalUnit _unit;

    [ObservableProperty]
    private RecurrenceBaseKind _baseKind;

    [ObservableProperty]
    private RecurrenceEndKind _endKind;

    /// <summary>終了日（ローカルの日付。時刻は使わない）。DatePicker と直接つなぐため DateTime で持つ。</summary>
    [ObservableProperty]
    private DateTime? _endDate;

    [ObservableProperty]
    private string _endCountText = "10";

    /// <summary>この画面で表せない RRULE（開いたときのまま保つ）。規則の入力を変えると捨てる。</summary>
    [ObservableProperty]
    private string? _rawRule;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _nextText;

    [ObservableProperty]
    private string? _afterNextText;

    [ObservableProperty]
    private string? _previewMessage;

    /// <summary>開いたときに繰り返しが付いていたか（「繰り返しをやめる」を出す）。</summary>
    [ObservableProperty]
    private bool _hadRecurrence;

    public RecurrencePickerModel(IRecurrenceEngine engine, IClock clock, bool weekStartsOnMonday = false)
    {
        _engine = engine;
        _clock = clock;
        var days = Enumerable.Range(0, 7).Select(i => (DayOfWeek)((i + (weekStartsOnMonday ? 1 : 0)) % 7)).ToList();
        Weekdays = [.. days.Select(d => new WeekdayToggle(d))];
        WeekdayOptions = [.. days.Select(d => new PickerOption<DayOfWeek>(d, $"{DateLabels.WeekdayName(d)}曜"))];
        foreach (var toggle in Weekdays)
        {
            toggle.PropertyChanged += (_, _) => OnRuleEdited();
        }
        Load(null, null, false);
    }

    public IReadOnlyList<WeekdayToggle> Weekdays { get; }

    public IReadOnlyList<PickerOption<DayOfWeek>> WeekdayOptions { get; }

    public static IReadOnlyList<PickerOption<int>> MonthDayOptions { get; } =
        [.. Enumerable.Range(1, 31).Select(d => new PickerOption<int>(d, $"{d}日")), new PickerOption<int>(-1, "月末")];

    public static IReadOnlyList<PickerOption<int>> NthOptions { get; } =
        [.. Enumerable.Range(1, 5).Select(n => new PickerOption<int>(n, $"第{n}")), new PickerOption<int>(-1, "最終")];

    public static IReadOnlyList<PickerOption<IntervalUnit>> UnitOptions { get; } =
    [
        new(IntervalUnit.Day, "日"),
        new(IntervalUnit.Week, "週"),
        new(IntervalUnit.Month, "か月"),
    ];

    public static IReadOnlyList<PickerOption<RecurrenceEndKind>> EndOptions { get; } =
    [
        new(RecurrenceEndKind.Never, "期限を決めない"),
        new(RecurrenceEndKind.UntilDate, "日付まで"),
        new(RecurrenceEndKind.Count, "回数まで"),
    ];

    /// <summary>プレビューの起点の日付（ローカル）。期限が無ければ今日。</summary>
    public DateOnly BaseDate { get; private set; }

    public bool IsRaw => RawRule is not null;

    /// <summary>読めない RRULE の説明（RecurrenceText が読めなければ RRULE そのもの）。</summary>
    public string? RawRuleText => RawRule is null ? null : $"いまの設定: {RecurrenceText.Describe(RawRule)}\n下の欄を変えると、この設定を置き換えます";

    public bool HasRule => Choice != RecurrenceChoice.None;

    public bool ShowsCustomWeekdays => Choice == RecurrenceChoice.Custom && Unit == IntervalUnit.Week;

    public string? WeekdayHint =>
        (Choice == RecurrenceChoice.Weekly || ShowsCustomWeekdays) && !Weekdays.Any(w => w.IsSelected)
            ? $"曜日を選ばないと、期限の曜日（{DateLabels.WeekdayName(BaseDate.DayOfWeek)}）で繰り返します"
            : null;

    public string? MonthDayHint =>
        Choice == RecurrenceChoice.Monthly && MonthlyMode == MonthlyMode.DayOfMonth && MonthDay >= 29
            ? $"{MonthDay}日がない月は飛ばします。毎月の最後の日にするなら「月末」を選びます"
            : null;

    public string YearlyHint => $"{BaseDate.Month}月{BaseDate.Day}日";

    public bool IsEndDate => EndKind == RecurrenceEndKind.UntilDate;

    public bool IsEndCount => EndKind == RecurrenceEndKind.Count;

    public bool CanCommit => Error is null;

    public bool HasPreviewDates => NextText is not null;

    /// <summary>
    /// 画面に写す。input=null は「なし」。baseDueUtc はプレビューの起点（null なら今日・終日）。
    /// 規則以外の欄（曜日・日付など）は起点の日付から既定値を入れておく（プリセットを切り替えたときに自然な値が出るように）。
    /// </summary>
    public void Load(RecurrenceInput? input, DateTime? baseDueUtc, bool dueHasTime)
    {
        _loading = true;
        try
        {
            _dueHasTime = baseDueUtc is not null && dueHasTime;
            _baseDueUtc = baseDueUtc ?? _clock.LocalDayStartUtc(_clock.LocalToday());
            BaseDate = _clock.ToLocalDate(_baseDueUtc);
            ResetRuleFields();
            HadRecurrence = input is not null;
            RawRule = null;
            BaseKind = input?.BaseKind ?? RecurrenceBaseKind.DueDate;
            EndKind = input?.EndKind ?? RecurrenceEndKind.Never;
            EndDate = input?.EndDate?.ToDateTime(TimeOnly.MinValue);
            EndCountText = input?.MaxOccurrences?.ToString(CultureInfo.InvariantCulture) ?? "10";
            if (input is null)
            {
                Choice = RecurrenceChoice.None;
            }
            else if (!TryApplyRule(input.RRule))
            {
                ResetRuleFields();
                Choice = RecurrenceChoice.Custom;
                RawRule = input.RRule;
            }
        }
        finally
        {
            _loading = false;
        }
        Refresh();
    }

    /// <summary>決定の値。「なし」は null。入力に誤りがあるときは呼ばない（CanCommit を見る）。</summary>
    public RecurrenceInput? Build()
    {
        if (Error is not null)
        {
            throw new InvalidOperationException(Error);
        }
        return BuildInput();
    }

    /// <summary>矢印キーでプリセットを1つ進める／戻す（端では反対側へ回る）。</summary>
    public void MoveChoice(int delta)
    {
        var index = Array.IndexOf(ChoiceOrder, Choice);
        Choice = ChoiceOrder[((index + delta) % ChoiceOrder.Length + ChoiceOrder.Length) % ChoiceOrder.Length];
    }

    public void ToggleMonthlyMode() =>
        MonthlyMode = MonthlyMode == MonthlyMode.DayOfMonth ? MonthlyMode.NthWeekday : MonthlyMode.DayOfMonth;

    public void ToggleBaseKind() =>
        BaseKind = BaseKind == RecurrenceBaseKind.DueDate ? RecurrenceBaseKind.CompletedDate : RecurrenceBaseKind.DueDate;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || !Inputs.Contains(e.PropertyName))
        {
            return;
        }
        if (RuleInputs.Contains(e.PropertyName))
        {
            RawRule = null;
        }
        if (e.PropertyName == nameof(EndKind) && EndKind == RecurrenceEndKind.UntilDate && EndDate is null)
        {
            EndDate = BaseDate.AddMonths(1).ToDateTime(TimeOnly.MinValue);   // ここで Refresh も走る
            return;
        }
        Refresh();
    }

    // 「毎月」の欄を触ったら、その欄の側（日付／曜日）を選んだことにする
    partial void OnMonthDayChanged(int value) => SwitchMonthlyMode(MonthlyMode.DayOfMonth);

    partial void OnMonthNthChanged(int value) => SwitchMonthlyMode(MonthlyMode.NthWeekday);

    partial void OnMonthWeekdayChanged(DayOfWeek value) => SwitchMonthlyMode(MonthlyMode.NthWeekday);

    private void SwitchMonthlyMode(MonthlyMode mode)
    {
        if (!_loading)
        {
            MonthlyMode = mode;
        }
    }

    private void OnRuleEdited()
    {
        if (_loading)
        {
            return;
        }
        RawRule = null;
        Refresh();
    }

    /// <summary>検証とプレビューをやり直す。</summary>
    private void Refresh()
    {
        Error = Validate();
        NextText = null;
        AfterNextText = null;
        if (Choice == RecurrenceChoice.None)
        {
            PreviewMessage = "繰り返しません";
        }
        else if (Error is not null)
        {
            PreviewMessage = Error;
        }
        else
        {
            var dates = _engine.Preview(BuildInput()!, _baseDueUtc, _dueHasTime, 2);
            PreviewMessage = dates.Count == 0 ? "この先の予定はありません" : null;
            if (dates.Count > 0)
            {
                NextText = Label(dates[0]);
                AfterNextText = dates.Count > 1 ? Label(dates[1]) : "なし（ここで終わります）";
            }
        }
        foreach (var name in Derived)
        {
            OnPropertyChanged(name);
        }
    }

    private string? Validate()
    {
        if (Choice == RecurrenceChoice.None)
        {
            return null;
        }
        if (Choice == RecurrenceChoice.Custom && RawRule is null && !TryParseCount(IntervalText, out _))
        {
            return $"間隔は 1〜{MaxInterval} の数で入れてください";
        }
        if (EndKind == RecurrenceEndKind.UntilDate && EndDate is null)
        {
            return "終わりの日を選んでください";
        }
        if (EndKind == RecurrenceEndKind.Count && !TryParseCount(EndCountText, out _))
        {
            return $"回数は 1〜{MaxInterval} の数で入れてください";
        }
        return null;
    }

    private RecurrenceInput? BuildInput()
    {
        if (BuildRRule() is not { } rrule)
        {
            return null;
        }
        return EndKind switch
        {
            RecurrenceEndKind.UntilDate when EndDate is { } end =>
                new RecurrenceInput(rrule, BaseKind, RecurrenceEndKind.UntilDate, EndDate: DateOnly.FromDateTime(end)),
            RecurrenceEndKind.Count when TryParseCount(EndCountText, out var count) =>
                new RecurrenceInput(rrule, BaseKind, RecurrenceEndKind.Count, MaxOccurrences: count),
            _ => new RecurrenceInput(rrule, BaseKind),
        };
    }

    private string? BuildRRule() => Choice switch
    {
        RecurrenceChoice.Daily => RecurrencePresets.Daily(),
        RecurrenceChoice.Weekdays => RecurrencePresets.Weekdays(),
        RecurrenceChoice.Weekly => RecurrencePresets.Weekly(SelectedDays()),
        RecurrenceChoice.Monthly => MonthlyMode == MonthlyMode.DayOfMonth
            ? RecurrencePresets.MonthlyByDay(MonthDay)
            : RecurrencePresets.MonthlyByWeekday(MonthNth, MonthWeekday),
        RecurrenceChoice.Yearly => RecurrencePresets.Yearly(),
        RecurrenceChoice.Custom => RawRule ?? BuildCustom(),
        _ => null,
    };

    private string? BuildCustom()
    {
        if (!TryParseCount(IntervalText, out var interval))
        {
            return null;
        }
        return Unit switch
        {
            IntervalUnit.Week => RecurrencePresets.Weekly(SelectedDays(), interval),
            IntervalUnit.Month => RecurrencePresets.Monthly(interval),
            _ => RecurrencePresets.Daily(interval),
        };
    }

    private List<DayOfWeek> SelectedDays() => [.. Weekdays.Where(w => w.IsSelected).Select(w => w.Day)];

    /// <summary>規則以外の欄を起点の日付から埋める（曜日＝起点の曜日、毎月＝起点の日・第N曜日）。</summary>
    private void ResetRuleFields()
    {
        foreach (var toggle in Weekdays)
        {
            toggle.IsSelected = toggle.Day == BaseDate.DayOfWeek;
        }
        MonthlyMode = MonthlyMode.DayOfMonth;
        MonthDay = BaseDate.Day;
        var nth = ((BaseDate.Day - 1) / 7) + 1;
        MonthNth = nth == 5 ? -1 : nth;
        MonthWeekday = BaseDate.DayOfWeek;
        IntervalText = "2";
        Unit = IntervalUnit.Day;
    }

    /// <summary>
    /// RRULE を画面の状態に写す。キーと値に分けて状態を作り、RecurrencePresets で組み直したものが元と一致すれば採用。
    /// 意味の解釈は組み直しの一致で確かめるので、ここで RRULE の意味を決めつけない。
    /// </summary>
    private bool TryApplyRule(string rrule)
    {
        var parts = Tokenize(rrule);
        if (parts is null || !parts.TryGetValue("FREQ", out var freq))
        {
            return false;
        }
        var interval = 1;
        if (parts.TryGetValue("INTERVAL", out var intervalText)
            && (!int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval is < 1 or > MaxInterval))
        {
            return false;
        }

        switch (freq)
        {
            case "DAILY":
                SetChoiceOrInterval(RecurrenceChoice.Daily, IntervalUnit.Day, interval);
                break;
            case "WEEKLY":
                if (!TrySelectDays(parts.GetValueOrDefault("BYDAY")))
                {
                    return false;
                }
                if (interval == 1 && string.Equals(rrule.Trim(), RecurrencePresets.Weekdays(), StringComparison.OrdinalIgnoreCase))
                {
                    Choice = RecurrenceChoice.Weekdays;
                }
                else
                {
                    SetChoiceOrInterval(RecurrenceChoice.Weekly, IntervalUnit.Week, interval);
                }
                break;
            case "MONTHLY" when parts.TryGetValue("BYMONTHDAY", out var monthDay):
                if (!int.TryParse(monthDay, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var day) || day is not (-1 or (>= 1 and <= 31)))
                {
                    return false;
                }
                Choice = RecurrenceChoice.Monthly;
                MonthlyMode = MonthlyMode.DayOfMonth;
                MonthDay = day;
                break;
            case "MONTHLY" when parts.TryGetValue("BYDAY", out var nthDay):
                if (!TryParseNthDay(nthDay, out var nth, out var weekday))
                {
                    return false;
                }
                Choice = RecurrenceChoice.Monthly;
                MonthlyMode = MonthlyMode.NthWeekday;
                MonthNth = nth;
                MonthWeekday = weekday;
                break;
            case "MONTHLY":
                // 日を指定しない毎月（期限の日で繰り返す）は「カスタム: Nか月ごと」で表す
                Choice = RecurrenceChoice.Custom;
                Unit = IntervalUnit.Month;
                IntervalText = interval.ToString(CultureInfo.InvariantCulture);
                break;
            case "YEARLY":
                Choice = RecurrenceChoice.Yearly;
                break;
            default:
                return false;
        }
        return string.Equals(BuildRRule(), rrule.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void SetChoiceOrInterval(RecurrenceChoice choice, IntervalUnit unit, int interval)
    {
        if (interval == 1)
        {
            Choice = choice;
            return;
        }
        Choice = RecurrenceChoice.Custom;
        Unit = unit;
        IntervalText = interval.ToString(CultureInfo.InvariantCulture);
    }

    private bool TrySelectDays(string? byDay)
    {
        var days = new HashSet<DayOfWeek>();
        foreach (var code in (byDay ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (DayOfCode(code) is not { } day)
            {
                return false;
            }
            days.Add(day);
        }
        foreach (var toggle in Weekdays)
        {
            toggle.IsSelected = days.Contains(toggle.Day);
        }
        return true;
    }

    /// <summary>"3MO" → (3, 月曜)、"-1FR" → (-1, 金曜)。</summary>
    private static bool TryParseNthDay(string value, out int nth, out DayOfWeek day)
    {
        day = default;
        nth = 0;
        if (value.Length <= 2
            || !int.TryParse(value[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nth)
            || nth is not (-1 or (>= 1 and <= 5))
            || DayOfCode(value[^2..]) is not { } parsed)
        {
            return false;
        }
        day = parsed;
        return true;
    }

    private static DayOfWeek? DayOfCode(string code) => code switch
    {
        "SU" => DayOfWeek.Sunday,
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        _ => null,
    };

    /// <summary>"FREQ=WEEKLY;BYDAY=MO" → キーと値（大文字）。形が崩れている・キーが重複するなら null。</summary>
    private static Dictionary<string, string>? Tokenize(string rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return null;
        }
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in rrule.Trim().ToUpperInvariant().Split(';'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !parts.TryAdd(part[..eq], part[(eq + 1)..]))
            {
                return null;
            }
        }
        return parts;
    }

    /// <summary>1〜999 の数。全角数字も受ける。</summary>
    private static bool TryParseCount(string? text, out int value) =>
        int.TryParse(TextNormalizer.ForName(text), NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && value is >= 1 and <= MaxInterval;

    private string Label(DateTime utc)
    {
        var local = _clock.ToLocal(utc);
        var text = DateLabels.Long(DateOnly.FromDateTime(local), _clock.LocalToday());
        return _dueHasTime ? $"{text} {DateLabels.Time(TimeOnly.FromDateTime(local))}" : text;
    }
}

/// <summary>日付の見せ方（繰り返しのプレビュー・テンプレート画面で共通）。</summary>
public static class DateLabels
{
    private const string Names = "日月火水木金土";

    public static char WeekdayName(DayOfWeek day) => Names[(int)day];

    /// <summary>「9月29日（月）」。今年でなければ「2027年1月5日（火）」。</summary>
    public static string Long(DateOnly date, DateOnly today) =>
        (date.Year == today.Year ? "" : $"{date.Year}年") + $"{date.Month}月{date.Day}日（{WeekdayName(date.DayOfWeek)}）";

    /// <summary>「9/18（木）」。</summary>
    public static string Short(DateOnly date) => $"{date.Month}/{date.Day}（{WeekdayName(date.DayOfWeek)}）";

    /// <summary>「9/18」。</summary>
    public static string MonthDay(DateOnly date) => $"{date.Month}/{date.Day}";

    /// <summary>「7:00」「20:00」。</summary>
    public static string Time(TimeOnly time) => time.ToString("H:mm", CultureInfo.InvariantCulture);
}

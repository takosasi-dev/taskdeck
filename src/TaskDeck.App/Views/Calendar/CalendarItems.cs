using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Entities;

namespace TaskDeck.App.Views.Calendar;

/// <summary>月表示か週表示か。</summary>
public enum CalendarMode
{
    Month,
    Week,
}

/// <summary>天気の絵（WMO の天気コードを大まかに分けたもの）。</summary>
public enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Rain,
    Snow,
    Thunder,
}

/// <summary>日付のセルに出す天気（F-195）。天気が取れていない日は作らない（null）。</summary>
public sealed record WeatherLook(WeatherKind Kind, string TemperatureText, string Description)
{
    /// <summary>WMO の天気コード → 絵。知らないコードは「くもり」にする。</summary>
    public static WeatherKind KindOf(int wmoCode) => wmoCode switch
    {
        0 or 1 => WeatherKind.Clear,
        2 => WeatherKind.PartlyCloudy,
        45 or 48 => WeatherKind.Fog,
        (>= 51 and <= 67) or (>= 80 and <= 82) => WeatherKind.Rain,
        (>= 71 and <= 77) or 85 or 86 => WeatherKind.Snow,
        >= 95 and <= 99 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy,
    };

    public static string NameOf(WeatherKind kind) => kind switch
    {
        WeatherKind.Clear => "晴れ",
        WeatherKind.PartlyCloudy => "晴れ時々くもり",
        WeatherKind.Fog => "霧",
        WeatherKind.Rain => "雨",
        WeatherKind.Snow => "雪",
        WeatherKind.Thunder => "雷雨",
        _ => "くもり",
    };

    public static WeatherLook? From(WeatherDay? day)
    {
        if (day is null)
        {
            return null;
        }
        var kind = KindOf(day.WeatherCode);
        var max = Degrees(day.TemperatureMax);
        var min = Degrees(day.TemperatureMin);
        var rain = day.PrecipitationProbability is { } p ? string.Create(CultureInfo.InvariantCulture, $" 降水 {p}%") : "";
        return new WeatherLook(
            kind,
            string.Create(CultureInfo.InvariantCulture, $"{max}°/{min}°"),
            string.Create(CultureInfo.InvariantCulture, $"{NameOf(kind)} 最高 {max}℃ 最低 {min}℃{rain}"));
    }

    // (int) で丸めるので -0.4℃ が「-0」にならない
    private static int Degrees(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>曜日の見出し（月表示の上の行）。</summary>
public sealed record CalendarWeekday(DayOfWeek Day)
{
    public string Name => "日月火水木金土"[(int)Day].ToString();

    public bool IsSunday => Day == DayOfWeek.Sunday;

    public bool IsSaturday => Day == DayOfWeek.Saturday;
}

/// <summary>
/// カレンダーに置く1件（期限のタスク、または繰り返しの仮表示）。月表示のチップ・週表示の終日帯と時間軸のブロックの中身。
/// 仮表示（IsGhost）は元のタスクを Task に持つ。押すと元のタスクを選ぶが、動かせない。
/// </summary>
public sealed partial class CalendarEntry : ObservableObject
{
    /// <summary>所要時間が無いタスクを週表示に置くときの長さ。</summary>
    public const int DefaultDurationMinutes = 30;

    /// <summary>これより短いブロックは時刻と題名を1行に詰める。</summary>
    public const int CompactBelowMinutes = 45;

    public required TaskItem Task { get; init; }

    /// <summary>置く日（ローカル）。</summary>
    public required DateOnly Date { get; init; }

    /// <summary>時刻（ローカル）。日付のみの期限は null。</summary>
    public required TimeOnly? Time { get; init; }

    public required bool IsGhost { get; init; }

    public required bool IsOverdue { get; init; }

    /// <summary>テーマに合わせたプロジェクト色（#RRGGBB）。プロジェクトなしは null（アクセント色で塗る）。</summary>
    public required string? ColorHex { get; init; }

    /// <summary>地に敷くプロジェクト色の薄い版（#AARRGGBB）。</summary>
    public required string? TintHex { get; init; }

    public required string AutomationName { get; init; }

    public Guid TaskId => Task.Id;

    public string Title => Task.Title;

    public bool HasTime => Time is not null;

    public string TimeText => Time is { } t ? t.ToString("H:mm", CultureInfo.InvariantCulture) : "";

    /// <summary>完了・中止（薄く出す）。仮表示は元が未完了なので false。</summary>
    public bool IsClosed => !IsGhost && !Task.IsOpen;

    public bool HasProjectColor => ColorHex is not null;

    public Priority Priority => Task.Priority;

    public bool HasPriority => Task.Priority != Priority.None;

    public string PrioritySymbol => DisplayText.PrioritySymbol(Task.Priority);

    /// <summary>週表示のブロックの長さ（分）。</summary>
    public int DurationMinutes => Task.DurationMinutes is { } m && m > 0 ? m : DefaultDurationMinutes;

    /// <summary>ブロックに添える「90分」（所要時間を入れたときだけ）。</summary>
    public string DurationText => Task.DurationMinutes is { } m && m > 0 ? string.Create(CultureInfo.InvariantCulture, $"{m}分") : "";

    public bool HasDurationText => DurationText.Length > 0;

    public bool IsCompact => DurationMinutes < CompactBelowMinutes;

    public int StartMinute => Time is { } t ? (t.Hour * 60) + t.Minute : 0;

    /// <summary>ドラッグで動かせるか（仮表示と、完了・中止は動かさない）。</summary>
    public bool CanDrag => !IsGhost && Task.IsOpen;

    [ObservableProperty]
    private bool _isSelected;

    public override string ToString() => AutomationName;
}

/// <summary>週表示の時間軸に置くブロック（重なったものは横に並べる。列は 0 から ColumnCount-1）。</summary>
public sealed class WeekBlock
{
    /// <summary>短いブロックも読めるよう、見た目の長さはこれより短くしない。</summary>
    public const int MinVisualMinutes = 20;

    public required CalendarEntry Entry { get; init; }

    public required int StartMinute { get; init; }

    /// <summary>見た目の終わり（その日の 24:00 で切る）。</summary>
    public required int EndMinute { get; init; }

    public required int Column { get; init; }

    public required int ColumnCount { get; init; }
}

/// <summary>1日ぶん（月表示のセル、または週表示の1列）。</summary>
public sealed class CalendarDay
{
    /// <summary>1日に見せるチップの上限（UI 設計書 5.3）。超えた分は「他 N 件」。</summary>
    public const int MaxVisibleEntries = 3;

    public required DateOnly Date { get; init; }

    public required bool IsToday { get; init; }

    /// <summary>月表示で、表示中の月の外の日。</summary>
    public required bool IsOtherMonth { get; init; }

    public required string? HolidayName { get; init; }

    public required WeatherLook? Weather { get; init; }

    /// <summary>月表示はその日の全部、週表示は終日（日付のみ）のものだけ。表示の順に並んでいる。</summary>
    public required IReadOnlyList<CalendarEntry> Entries { get; init; }

    public required IReadOnlyList<CalendarEntry> VisibleEntries { get; init; }

    /// <summary>週表示の時間軸に置くもの（月表示では空）。</summary>
    public required IReadOnlyList<WeekBlock> Blocks { get; init; }

    // 見せるチップ3つ（セルの中のボタンを作り直さず、中身だけを差し替えるための固定の枠。無ければ null）
    public CalendarEntry? Chip1 => VisibleEntries.ElementAtOrDefault(0);

    public CalendarEntry? Chip2 => VisibleEntries.ElementAtOrDefault(1);

    public CalendarEntry? Chip3 => VisibleEntries.ElementAtOrDefault(2);

    public int MoreCount => Entries.Count - VisibleEntries.Count;

    public bool HasMore => MoreCount > 0;

    public string MoreText => string.Create(CultureInfo.InvariantCulture, $"他 {MoreCount} 件");

    public string MoreAutomationName => string.Create(CultureInfo.InvariantCulture, $"{Date.Month}月{Date.Day}日の他 {MoreCount} 件");

    /// <summary>セルの日付（表示中の月の外の1日だけ「10/1」と月を添える）。</summary>
    public string DayText => IsOtherMonth && Date.Day == 1
        ? string.Create(CultureInfo.InvariantCulture, $"{Date.Month}/{Date.Day}")
        : Date.Day.ToString(CultureInfo.InvariantCulture);

    public string WeekdayText => DisplayText.Weekday(Date);

    /// <summary>「9月23日（水）」。</summary>
    public string DateText => DisplayText.DateWithWeekday(Date);

    public bool HasHoliday => HolidayName is not null;

    /// <summary>日曜・祝日は休日の色（F-192）。</summary>
    public bool IsHolidayColor => HolidayName is not null || Date.DayOfWeek == DayOfWeek.Sunday;

    /// <summary>祝日でない土曜はアクセントの色。</summary>
    public bool IsSaturdayColor => !IsHolidayColor && Date.DayOfWeek == DayOfWeek.Saturday;

    /// <summary>このセルの全部（週表示では終日と時間軸の両方）。選択の強調に使う。</summary>
    public IEnumerable<CalendarEntry> AllEntries => Entries.Concat(Blocks.Select(b => b.Entry));

    /// <summary>見せる数だけを変えた写し（窓が低くてセルに3件入らないとき）。</summary>
    public CalendarDay WithMaxVisible(int maxVisible) => new()
    {
        Date = Date,
        IsToday = IsToday,
        IsOtherMonth = IsOtherMonth,
        HolidayName = HolidayName,
        Weather = Weather,
        Entries = Entries,
        VisibleEntries = Entries.Count > maxVisible ? [.. Entries.Take(maxVisible)] : Entries,
        Blocks = Blocks,
    };
}

/// <summary>
/// 画面のセルの枠（月表示の42個・週表示の7個）。枠は作ったまま使い回し、読み込むたびに Day だけを入れ替える。
/// 月を切り替えるたびにセルとチップの見た目を作り直すと、1回で 0.5〜1 秒かかるため（1万件のデータで計測）。
/// </summary>
public sealed partial class CalendarCell : ObservableObject
{
    [ObservableProperty]
    private CalendarDay? _day;
}

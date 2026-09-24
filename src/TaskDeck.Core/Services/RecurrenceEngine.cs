using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services.Recurrence;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.Core.Services;

/// <summary>
/// 繰り返しの計算（設計書 4.1）。Ical.Net で RRULE を<b>ローカル時刻</b>で評価する。
///
/// 決めごと:
/// - 次回を数える起点は、期限日基準＝元の期限（無ければルールの AnchorAt）、完了日基準＝完了日時のローカル日付。
///   時刻ありのタスクは、完了日基準でも<b>元の期限の時刻</b>を保つ（「3日ごと 15:00」が完了時刻に引きずられない）
/// - 日付のみの期限は結果もローカル 0:00 の UTC、時刻ありはその時刻のまま UTC に直す
/// - 終了条件: EndDate はローカル日付でその日を含む（超えたら次回なし）。回数は CompletedCount+1 >= MaxOccurrences で打ち切り
/// - 「平日のみ」（<see cref="RecurrencePresets.IsWeekdaysOnly"/>）の発生日が祝日で、設定 ShiftWeekdayRecurrenceOnHolidays が ON なら翌営業日へ送る（F-193）。
///   送った結果が前の回と重なったら、その回は飛ばす
/// </summary>
public sealed class RecurrenceEngine(IClock clock, IHolidayProvider holidays, ISettingsStore settings) : IRecurrenceEngine
{
    public DateTime? NextDue(TaskItem task, RecurrenceRule rule, DateTime completedAtUtc)
    {
        if (IsCountExhausted(rule))
        {
            return null;
        }
        var hasTime = task.DueHasTime && task.DueAt is not null;
        var baseLocal = rule.BaseKind == RecurrenceBaseKind.CompletedDate
            ? CompletedBase(task, completedAtUtc, hasTime)
            : DueBase(task, rule, hasTime);
        return FirstUtcAfter(rule.RRule, baseLocal, hasTime, EndDateOf(rule));
    }

    public DateTime? NextDueForSkip(TaskItem task, RecurrenceRule rule)
    {
        // スキップは「今回分を完了にせず次へ送る」（要件 F-037）。完了日基準のルールでも、送り先は期限日から数える
        if (IsCountExhausted(rule))
        {
            return null;
        }
        var hasTime = task.DueHasTime && task.DueAt is not null;
        return FirstUtcAfter(rule.RRule, DueBase(task, rule, hasTime), hasTime, EndDateOf(rule));
    }

    public IReadOnlyList<DateTime> Preview(RecurrenceInput rule, DateTime baseDueUtc, bool dueHasTime, int count)
    {
        if (count <= 0)
        {
            return [];
        }
        // 回数指定では、基準にしている期限そのものが1回目なので、この先に出るのは MaxOccurrences-1 回まで
        var remaining = rule.EndKind == RecurrenceEndKind.Count && rule.MaxOccurrences is { } max
            ? Math.Max(0, max - 1)
            : (int?)null;
        var endDate = rule.EndKind == RecurrenceEndKind.UntilDate ? rule.EndDate : null;
        var take = remaining is { } r ? Math.Min(count, r) : count;
        return
        [
            .. LocalOccurrencesAfter(rule.RRule, LocalBase(baseDueUtc, dueHasTime), dueHasTime, endDate, take)
                .Select(local => ToUtc(local, dueHasTime)),
        ];
    }

    public IReadOnlyList<DateTime> Occurrences(RecurrenceRule rule, DateTime currentDueUtc, bool dueHasTime, DateTime fromUtc, DateTime toUtc)
    {
        // 回数指定では、いま開いている実体タスク（currentDue）も1回ぶんとして数える
        var remaining = rule.EndKind == RecurrenceEndKind.Count && rule.MaxOccurrences is { } max
            ? Math.Max(0, max - (rule.CompletedCount + 1))
            : (int?)null;
        var result = new List<DateTime>();
        foreach (var local in LocalOccurrencesAfter(rule.RRule, LocalBase(currentDueUtc, dueHasTime), dueHasTime, EndDateOf(rule), remaining))
        {
            var utc = ToUtc(local, dueHasTime);
            if (utc >= toUtc)
            {
                break;
            }
            if (utc >= fromUtc)
            {
                result.Add(utc);
            }
        }
        return result;
    }

    /// <summary>baseLocal より後の発生日時（ローカル）。祝日送り・終了日・回数の打ち切りを反映する。</summary>
    private IEnumerable<DateTime> LocalOccurrencesAfter(string rrule, DateTime baseLocal, bool hasTime, DateOnly? endDate, int? maxCount)
    {
        if (maxCount is 0)
        {
            yield break;
        }
        var shiftHolidays = settings.Current.External.ShiftWeekdayRecurrenceOnHolidays && RecurrencePresets.IsWeekdaysOnly(rrule);
        var last = baseLocal;
        var emitted = 0;
        foreach (var raw in RruleEvaluator.After(rrule, baseLocal, hasTime))
        {
            var occurrence = shiftHolidays ? ShiftToBusinessDay(raw) : raw;
            if (occurrence <= last)
            {
                // 祝日送りで前の回に追いついた（月曜が祝日で火曜に送られ、火曜の回と重なった）
                continue;
            }
            if (endDate is { } end && DateOnly.FromDateTime(occurrence) > end)
            {
                yield break;
            }
            last = occurrence;
            yield return occurrence;
            if (maxCount is { } max && ++emitted >= max)
            {
                yield break;
            }
        }
    }

    private DateTime ShiftToBusinessDay(DateTime local)
    {
        var date = DateOnly.FromDateTime(local);
        var moved = BusinessDays.OnOrAfter(date, holidays);
        return moved == date ? local : moved.ToDateTime(TimeOnly.FromDateTime(local));
    }

    private DateTime? FirstUtcAfter(string rrule, DateTime baseLocal, bool hasTime, DateOnly? endDate)
    {
        foreach (var local in LocalOccurrencesAfter(rrule, baseLocal, hasTime, endDate, null))
        {
            return ToUtc(local, hasTime);
        }
        return null;
    }

    /// <summary>期限日基準の起点。期限が無いタスクはルールの AnchorAt（＝作成日時）のローカル日付から数える。</summary>
    private DateTime DueBase(TaskItem task, RecurrenceRule rule, bool hasTime)
    {
        if (task.DueAt is not { } due)
        {
            return clock.ToLocal(rule.AnchorAt).Date;
        }
        var local = clock.ToLocal(due);
        return hasTime ? local : local.Date;
    }

    /// <summary>完了日基準の起点。日付は完了日、時刻は元の期限の時刻を保つ。</summary>
    private DateTime CompletedBase(TaskItem task, DateTime completedAtUtc, bool hasTime)
    {
        var day = clock.ToLocal(completedAtUtc).Date;
        return hasTime && task.DueAt is { } due ? day.Add(clock.ToLocal(due).TimeOfDay) : day;
    }

    private DateTime LocalBase(DateTime utc, bool hasTime)
    {
        var local = clock.ToLocal(utc);
        return hasTime ? local : local.Date;
    }

    private DateTime ToUtc(DateTime local, bool hasTime) =>
        hasTime ? clock.ToUtc(local) : clock.LocalDayStartUtc(DateOnly.FromDateTime(local));

    private static DateOnly? EndDateOf(RecurrenceRule rule) =>
        rule.EndKind == RecurrenceEndKind.UntilDate ? rule.EndDate : null;

    private static bool IsCountExhausted(RecurrenceRule rule) =>
        rule.EndKind == RecurrenceEndKind.Count && rule.MaxOccurrences is { } max && rule.CompletedCount + 1 >= max;
}

/// <summary>RRULE を日本語で表す（詳細ペイン・一覧のアイコンのツールチップ用）。</summary>
public static class RecurrenceText
{
    private const string DayNames = "日月火水木金土";

    private static readonly string[] KnownKeys = ["FREQ", "INTERVAL", "BYDAY", "BYMONTHDAY", "WKST"];

    /// <summary>
    /// 例: "FREQ=DAILY"→「毎日」、"FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"→「平日」、"FREQ=WEEKLY;BYDAY=MO"→「毎週 月」、
    /// "FREQ=MONTHLY;BYMONTHDAY=1"→「毎月 1日」、"FREQ=MONTHLY;BYMONTHDAY=-1"→「毎月 月末」、
    /// "FREQ=MONTHLY;BYDAY=3MO"→「毎月 第3月曜」、"FREQ=DAILY;INTERVAL=3"→「3日ごと」、"FREQ=YEARLY"→「毎年」。
    /// 読めない形（未対応のキーが混じる・値が壊れている）は RRULE をそのまま返す。
    /// </summary>
    public static string Describe(string rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return rrule;
        }
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in rrule.ToUpperInvariant().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !KnownKeys.Contains(part[..eq]))
            {
                return rrule;
            }
            parts[part[..eq]] = part[(eq + 1)..];
        }

        var interval = 1;
        if (parts.TryGetValue("INTERVAL", out var intervalText) && (!int.TryParse(intervalText, out interval) || interval < 1))
        {
            return rrule;
        }

        return parts.GetValueOrDefault("FREQ") switch
        {
            "DAILY" when !parts.ContainsKey("BYDAY") && !parts.ContainsKey("BYMONTHDAY") =>
                interval == 1 ? "毎日" : $"{interval}日ごと",
            "WEEKLY" when !parts.ContainsKey("BYMONTHDAY") => DescribeWeekly(parts.GetValueOrDefault("BYDAY"), interval, rrule),
            "MONTHLY" => DescribeMonthly(parts, interval, rrule),
            "YEARLY" when !parts.ContainsKey("BYDAY") && !parts.ContainsKey("BYMONTHDAY") =>
                interval == 1 ? "毎年" : $"{interval}年ごと",
            _ => rrule,
        };
    }

    private static string DescribeWeekly(string? byDay, int interval, string rrule)
    {
        var prefix = interval switch { 1 => "毎週", 2 => "隔週", _ => $"{interval}週ごと" };
        if (string.IsNullOrEmpty(byDay))
        {
            return prefix;
        }
        var days = new List<char>();
        foreach (var code in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var day = DayOfCode(code);
            if (day is null)
            {
                return rrule;
            }
            days.Add(DayNames[(int)day.Value]);
        }
        if (interval == 1 && byDay is "MO,TU,WE,TH,FR")
        {
            return "平日";
        }
        return $"{prefix} {string.Join('・', days)}";
    }

    private static string DescribeMonthly(Dictionary<string, string> parts, int interval, string rrule)
    {
        var prefix = interval == 1 ? "毎月" : $"{interval}か月ごと";
        if (parts.TryGetValue("BYMONTHDAY", out var monthDay))
        {
            if (parts.ContainsKey("BYDAY") || !int.TryParse(monthDay, out var day) || day is 0 or < -1 or > 31)
            {
                return rrule;
            }
            return day == -1 ? $"{prefix} 月末" : $"{prefix} {day}日";
        }
        if (parts.TryGetValue("BYDAY", out var byDay))
        {
            var offsetLength = byDay.Length - 2;
            if (offsetLength <= 0 || !int.TryParse(byDay[..offsetLength], out var nth) || DayOfCode(byDay[offsetLength..]) is not { } day)
            {
                return rrule;
            }
            var name = DayNames[(int)day];
            return nth == -1 ? $"{prefix} 最終{name}曜" : nth is >= 1 and <= 5 ? $"{prefix} 第{nth}{name}曜" : rrule;
        }
        return prefix;
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
}

/// <summary>UI のプリセットから RRULE を作る。</summary>
public static class RecurrencePresets
{
    private static readonly string[] ByDayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public static string Daily(int interval = 1) =>
        interval <= 1 ? "FREQ=DAILY" : $"FREQ=DAILY;INTERVAL={interval}";

    public static string Weekdays() => "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR";

    /// <summary>曜日指定の毎週（interval 週ごと）。days が空なら曜日指定なし（期限の曜日で繰り返す）。</summary>
    public static string Weekly(IReadOnlyCollection<DayOfWeek> days, int interval = 1)
    {
        var rule = interval <= 1 ? "FREQ=WEEKLY" : $"FREQ=WEEKLY;INTERVAL={interval}";
        if (days.Count == 0)
        {
            return rule;
        }
        var codes = days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => ByDayCodes[(int)d]);
        return $"{rule};BYDAY={string.Join(',', codes)}";
    }

    /// <summary>日を指定しない毎月（期限の日で繰り返す）。</summary>
    public static string Monthly(int interval = 1) =>
        interval <= 1 ? "FREQ=MONTHLY" : $"FREQ=MONTHLY;INTERVAL={interval}";

    /// <summary>毎月の指定日。day=-1 で月末。</summary>
    public static string MonthlyByDay(int day) => $"FREQ=MONTHLY;BYMONTHDAY={day}";

    /// <summary>毎月第 nth 曜日。nth=-1 で最終。</summary>
    public static string MonthlyByWeekday(int nth, DayOfWeek day) => $"FREQ=MONTHLY;BYDAY={nth}{ByDayCodes[(int)day]}";

    public static string Yearly() => "FREQ=YEARLY";

    /// <summary>「平日のみ」の繰り返しか（祝日送りの対象）。</summary>
    public static bool IsWeekdaysOnly(string rrule)
    {
        var parts = rrule.ToUpperInvariant().Split(';', StringSplitOptions.RemoveEmptyEntries);
        var byDay = parts.FirstOrDefault(p => p.StartsWith("BYDAY=", StringComparison.Ordinal));
        if (byDay is null)
        {
            return false;
        }
        var days = byDay["BYDAY=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        return days.Length > 0 && days.All(d => d is "MO" or "TU" or "WE" or "TH" or "FR");
    }
}

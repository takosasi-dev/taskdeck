using System.Text.RegularExpressions;

namespace TaskDeck.Core.Services.QuickInput;

/// <summary>
/// クイック入力の <c>*繰り返し</c>（要件 3.7）を RRULE に直す。読めなければ null（タイトルに残して警告を出す）。
/// 曜日は「月」「月曜」「月曜日」「月水金」「月・水・金」「火曜と木曜」のどれでも受ける。
/// </summary>
internal static partial class RecurrenceWords
{
    private const string DayChars = "日月火水木金土";

    public static string? ToRRule(string body)
    {
        var s = body.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        switch (s)
        {
            case "毎日": return RecurrencePresets.Daily();
            case "平日" or "毎平日": return RecurrencePresets.Weekdays();
            case "毎年": return RecurrencePresets.Yearly();
            case "月末" or "毎月末" or "毎月月末": return RecurrencePresets.MonthlyByDay(-1);
            case "毎月": return RecurrencePresets.Monthly();
            case "毎週": return RecurrencePresets.Weekly([]);
            default: break;
        }

        if (EveryNDays().Match(s) is { Success: true } daily && int.TryParse(daily.Groups["n"].Value, out var days) && days >= 1)
        {
            return RecurrencePresets.Daily(days);
        }
        if (EveryNWeeks().Match(s) is { Success: true } weeks && int.TryParse(weeks.Groups["n"].Value, out var weekInterval) && weekInterval >= 1)
        {
            return ParseDays(weeks.Groups["days"].Value) is { } d ? RecurrencePresets.Weekly(d, weekInterval) : null;
        }
        if (EveryNMonths().Match(s) is { Success: true } months && int.TryParse(months.Groups["n"].Value, out var monthInterval) && monthInterval >= 1)
        {
            return RecurrencePresets.Monthly(monthInterval);
        }
        if (Weekly().Match(s) is { Success: true } weekly)
        {
            var interval = weekly.Groups["every"].Value == "隔週" ? 2 : 1;
            return ParseDays(weekly.Groups["days"].Value) is { } d ? RecurrencePresets.Weekly(d, interval) : null;
        }
        if (MonthlyByDay().Match(s) is { Success: true } monthDay && int.TryParse(monthDay.Groups["d"].Value, out var day) && day is >= 1 and <= 31)
        {
            return RecurrencePresets.MonthlyByDay(day);
        }
        if (MonthlyByWeekday().Match(s) is { Success: true } nth && ParseDays(nth.Groups["day"].Value) is [var weekday])
        {
            var last = nth.Groups["last"].Success;
            if (last)
            {
                return RecurrencePresets.MonthlyByWeekday(-1, weekday);
            }
            return int.TryParse(nth.Groups["n"].Value, out var n) && n is >= 1 and <= 5
                ? RecurrencePresets.MonthlyByWeekday(n, weekday)
                : null;
        }

        return null;
    }

    /// <summary>「月」「月曜」「月曜日」「月水金」「月・水・金」「火曜と木曜」→ 曜日の並び。読めなければ null。空文字は空の並び。</summary>
    private static List<DayOfWeek>? ParseDays(string s)
    {
        var days = new List<DayOfWeek>();
        for (var i = 0; i < s.Length;)
        {
            if (s[i] is '・' or ',' or '、' or 'と' or ' ')
            {
                i++;
                continue;
            }
            var index = DayChars.IndexOf(s[i], StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }
            days.Add((DayOfWeek)index);
            i++;
            // 「月曜」「月曜日」の接尾辞を読み飛ばす（「日曜日」の最後の「日」を曜日と取り違えない）
            if (i < s.Length && s[i] == '曜')
            {
                i++;
                if (i < s.Length && s[i] == '日')
                {
                    i++;
                }
            }
        }
        return days;
    }

    [GeneratedRegex(@"^(?<n>\d{1,3})日ごと$")]
    private static partial Regex EveryNDays();

    [GeneratedRegex(@"^(?<n>\d{1,3})週(間)?ごと(?<days>.*)$")]
    private static partial Regex EveryNWeeks();

    [GeneratedRegex(@"^(?<n>\d{1,3})[かヵヶカケ]?月ごと$")]
    private static partial Regex EveryNMonths();

    [GeneratedRegex(@"^(?<every>毎週|隔週)(?<days>.+)$")]
    private static partial Regex Weekly();

    [GeneratedRegex(@"^毎月(?<d>\d{1,2})日$")]
    private static partial Regex MonthlyByDay();

    [GeneratedRegex(@"^毎月(第(?<n>\d)|(?<last>最終|最後の))(?<day>.+)$")]
    private static partial Regex MonthlyByWeekday();
}

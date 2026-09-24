using System.Text.RegularExpressions;

namespace TaskDeck.Core.Services.QuickInput;

/// <summary>正規化後の文字列の中で見つけた日付・時刻（位置は正規化後の添字）。</summary>
internal readonly record struct WordMatch<T>(T Value, int Start, int Length);

/// <summary>
/// クイック入力の日付・時刻表現（設計書 4.2 の日付表、要件 3.7）。
/// どちらも<b>いちばん後ろの解釈できた表現</b>を採る（「文字列末尾から順に剥がす」という規則の実装）。
/// 空白で区切られていなくても拾うので、「ゴミを出す明日」も読める。
/// </summary>
internal static partial class DateTimeWords
{
    private const string DayChars = "日月火水木金土";

    /// <summary>日付・時刻のすぐ後ろに続く助詞は、いっしょに取り除いてタイトルを汚さない（「明日の会議」→「会議」）。</summary>
    private static readonly string[] TailWords = ["中", "まで"];

    public static WordMatch<TimeOnly>? FindLastTime(string text)
    {
        WordMatch<TimeOnly>? best = null;
        foreach (var match in TimeWithColon().Matches(text).Concat(TimeWithKanji().Matches(text)))
        {
            if (ResolveTime(match) is not { } time || (best is { } b && match.Index < b.Start))
            {
                continue;
            }
            best = new WordMatch<TimeOnly>(time, match.Index, ExtendTail(text, match.Index + match.Length) - match.Index);
        }
        return best;
    }

    public static WordMatch<DateOnly>? FindLastDate(string text, DateOnly today)
    {
        WordMatch<DateOnly>? best = null;
        foreach (Match match in DateWords().Matches(text))
        {
            if (ResolveDate(match, today) is not { } date)
            {
                continue;
            }
            best = new WordMatch<DateOnly>(date, match.Index, ExtendTail(text, match.Index + match.Length) - match.Index);
        }
        return best;
    }

    private static TimeOnly? ResolveTime(Match match)
    {
        if (!int.TryParse(match.Groups["h"].Value, out var hour))
        {
            return null;
        }
        var minute = 0;
        if (match.Groups["han"].Success)
        {
            minute = 30;
        }
        else if (match.Groups["m"].Success && !int.TryParse(match.Groups["m"].Value, out minute))
        {
            return null;
        }
        if (minute > 59)
        {
            return null;
        }
        var meridiem = match.Groups["ap"].Value;
        if (meridiem.Length > 0)
        {
            // 午前12時＝0時、午後12時＝12時
            if (hour is < 1 or > 12)
            {
                return null;
            }
            hour = meridiem == "午後" ? (hour == 12 ? 12 : hour + 12) : (hour == 12 ? 0 : hour);
        }
        return hour > 23 ? null : new TimeOnly(hour, minute);
    }

    private static DateOnly? ResolveDate(Match match, DateOnly today)
    {
        if (match.Groups["d0"].Success)
        {
            return today;
        }
        if (match.Groups["d1"].Success)
        {
            return today.AddDays(1);
        }
        if (match.Groups["d2"].Success)
        {
            return today.AddDays(2);
        }
        if (match.Groups["weekend"].Success)
        {
            return OnOrAfterWeekday(today, DayOfWeek.Saturday);
        }
        if (match.Groups["monthend"].Success)
        {
            return new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        }
        if (match.Groups["nextwd"].Success)
        {
            return InNextWeek(today, WeekdayOf(match.Groups["nw"].Value[0]));
        }
        if (match.Groups["wd"].Success)
        {
            return OnOrAfterWeekday(today, WeekdayOf(match.Groups["w"].Value[0]));
        }
        if (match.Groups["barewd"].Success)
        {
            return BareWeekday(match.Groups["bw"].Value) is { } day ? OnOrAfterWeekday(today, day) : null;
        }
        if (match.Groups["ndays"].Success)
        {
            return int.TryParse(match.Groups["n"].Value, out var n) ? today.AddDays(n) : null;
        }
        return ResolveCalendarDate(match, today);
    }

    /// <summary>「9/25」「9月25日」「2026/9/25」。年の指定が無ければ今年、過去なら翌年（2/29 は次のうるう年）。</summary>
    private static DateOnly? ResolveCalendarDate(Match match, DateOnly today)
    {
        if (!int.TryParse(match.Groups["mo"].Value, out var month) || !int.TryParse(match.Groups["da"].Value, out var day) || month is < 1 or > 12)
        {
            return null;
        }
        if (match.Groups["y"].Success)
        {
            var year = int.Parse(match.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture);
            return year is >= 1 and <= 9999 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
                ? new DateOnly(year, month, day)
                : null;
        }
        for (var i = 0; i < 8 && today.Year + i <= 9999; i++)
        {
            var year = today.Year + i;
            if (day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                continue;
            }
            var date = new DateOnly(year, month, day);
            if (date >= today)
            {
                return date;
            }
        }
        return null;
    }

    /// <summary>date 当日を含めて、次に来るその曜日（今日がその曜日なら今日）。</summary>
    private static DateOnly OnOrAfterWeekday(DateOnly date, DayOfWeek day) =>
        date.AddDays(((int)day - (int)date.DayOfWeek + 7) % 7);

    /// <summary>翌週（月曜始まりで数える）のその曜日。</summary>
    private static DateOnly InNextWeek(DateOnly date, DayOfWeek day)
    {
        var mondayOfThisWeek = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return mondayOfThisWeek.AddDays(7 + (((int)day + 6) % 7));
    }

    private static DayOfWeek WeekdayOf(char c) => (DayOfWeek)DayChars.IndexOf(c, StringComparison.Ordinal);

    private static DayOfWeek? BareWeekday(string s) => s switch
    {
        "げつ" => DayOfWeek.Monday,
        "すい" => DayOfWeek.Wednesday,
        "もく" => DayOfWeek.Thursday,
        "きん" => DayOfWeek.Friday,
        "ど" => DayOfWeek.Saturday,
        "にち" => DayOfWeek.Sunday,
        _ => s.Length == 1 && DayChars.Contains(s[0], StringComparison.Ordinal) ? WeekdayOf(s[0]) : null,
    };

    private static int ExtendTail(string text, int end)
    {
        foreach (var word in TailWords)
        {
            if (text.AsSpan(end).StartsWith(word, StringComparison.Ordinal))
            {
                end += word.Length;
                break;
            }
        }
        return end < text.Length && text[end] is 'に' or 'の' ? end + 1 : end;
    }

    [GeneratedRegex(@"(?<![0-9:])(?:(?<ap>午前|午後)\s?)?(?<h>\d{1,2}):(?<m>\d{2})(?![0-9:])")]
    private static partial Regex TimeWithColon();

    // 「3時間後」を時刻と取らないように、時のあとに「間」が来る形は除く
    [GeneratedRegex(@"(?<![0-9])(?:(?<ap>午前|午後)\s?)?(?<h>\d{1,2})時(?!間)(?:(?<m>\d{1,2})分|(?<han>半))?")]
    private static partial Regex TimeWithKanji();

    [GeneratedRegex(
        """
        (?<slash>(?:(?<y>\d{4})[/年])?(?<mo>\d{1,2})[/月](?<da>\d{1,2})日?)
        |(?<d0>今日|きょう|本日)
        |(?<d2>明後日|あさって)
        |(?<d1>明日|あした|あす)
        |(?<![来先])(?<weekend>今週末|週末)
        |(?<![来先0-9])(?<monthend>月末)
        |(?<nextwd>来週\s?(?<nw>[日月火水木金土])曜?日?)
        |(?<wd>(?<w>[日月火水木金土])曜日?)
        |(?<ndays>(?<n>\d{1,3})日後)
        |(?<![^\s])(?<barewd>(?<bw>げつ|すい|もく|きん|にち|ど|[日月火水木金土]))(?![^\s])
        """,
        RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex DateWords();
}

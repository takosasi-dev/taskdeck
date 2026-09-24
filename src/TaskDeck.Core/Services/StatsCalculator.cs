using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Time;

namespace TaskDeck.Core.Services;

public enum StatsPeriod
{
    ThisWeek,
    ThisMonth,
    ThisYear,
}

/// <summary>推移グラフの棒1本（週・月は日ごと、年は月ごと）。IsFuture は未来の区間（0件で描く）。</summary>
public sealed record TrendBar(DateOnly Start, DateOnly End, string Label, int Count, bool IsFuture);

/// <summary>プロジェクト別の内訳。上位4件＋「その他」（IsOther）。ProjectId=null・IsOther=false は「プロジェクトなし」。</summary>
public sealed record ProjectShare(Guid? ProjectId, string Name, string? ColorHex, int Count, bool IsOther);

/// <summary>ヒートマップの1マス。Level は 0（0件）〜4。</summary>
public sealed record HeatCell(DateOnly Date, int Count, int Level);

/// <summary>
/// 振り返りの指標（設計書 4.6、要件 3.16）。日付はすべてローカル。
/// ChangeRatio は前期間比（前期間0件なら null）。OnTime は「期限なし、または期限内に完了」÷ 完了数。
/// WeekdayAverages は日曜=0〜土曜=6 の、期間内のその曜日1日あたりの平均完了数。
/// Heatmap は今日を含む直近26週（週の開始曜日にそろえる）。
/// </summary>
public sealed record StatsReport(
    StatsPeriod Period,
    DateOnly From,
    DateOnly To,
    int ElapsedDays,
    int CompletedCount,
    int PreviousCompletedCount,
    double? ChangeRatio,
    int OnTimeCount,
    int OnTimeDenominator,
    int CurrentStreak,
    int LongestStreak,
    double? AverageLeadDays,
    IReadOnlyList<TrendBar> Trend,
    IReadOnlyList<ProjectShare> Projects,
    IReadOnlyList<double> WeekdayAverages,
    IReadOnlyList<HeatCell> Heatmap)
{
    /// <summary>前期間の平均リードタイム（「先週の平均は 2.6 日」の比較用）。前期間に完了が無ければ null。</summary>
    public double? PreviousAverageLeadDays { get; init; }
}

/// <summary>
/// 振り返りの集計（純粋関数）。facts は<b>全期間</b>の完了タスク（Status=Completed・削除済みを除く）。
/// 中止（Cancelled）は「終わらせた」と言えないので facts に来ない前提（設計書 4.6）。
///
/// 決めごと:
/// - 日付はすべて<b>ローカル日付</b>に振り分けてから数える（深夜0時過ぎの完了が前日に入らないように）
/// - 推移の粒度: 今週＝直近14日の日ごと（モックアップ Stats.dc.html の「直近 14 日」）、今月＝その月の日ごと、今年＝月ごと12本。
///   期間に未来が含まれるときは 0 件の棒を IsFuture で描く（軸を月末・年末まで伸ばす）
/// - 前期間は「先週・先月・去年」まるごと（途中経過どうしでは比べない）。前期間0件なら比較は null
/// - 期限内完了: 期限なし、時刻ありは CompletedAt ≤ DueAt、日付のみは<b>その日のうち</b>（ローカル 23:59:59）まで
/// - 連続日数は今日から遡って数え、<b>今日がまだ0件でも途切れさせない</b>（1日の途中で0になるのを防ぐ）。自己最長は全期間から
/// - 平均リードタイムは期間内の完了ぶん。作成日時が完了日時より後の壊れたデータは 0 日として数える
/// - プロジェクト別は件数の多い順に4件＋「その他」。「プロジェクトなし」は1つの内訳として順位に加わり、
///   すでに消えたプロジェクト（projects に無い Id）は名前を出せないので「その他」に寄せる
/// - 曜日別は、期間のうち<b>今日までに過ぎた</b>その曜日の日数で割る（未来の0件で薄めない）
/// - ヒートマップは週の開始曜日にそろえた26週ぶん。今週の未来日はマスを作らない。
///   段階は 0件=0、それ以外は期間内の最大件数に対する比（ceil(4×件数÷最大)）で1〜4
/// </summary>
public sealed class StatsCalculator(IClock clock)
{
    public const int HeatmapWeeks = 26;

    /// <summary>「今週」の推移で見せる日数（モックアップの「直近 14 日」）。</summary>
    public const int WeekTrendDays = 14;

    public StatsReport Calculate(
        IReadOnlyList<CompletedTaskFact> facts,
        StatsPeriod period,
        IReadOnlyList<Project> projects,
        bool weekStartsOnMonday)
    {
        var today = clock.LocalToday();
        var (from, to) = PeriodRange(period, today, weekStartsOnMonday);
        var elapsedTo = to < today ? to : today;
        var elapsedDays = elapsedTo < from ? 0 : elapsedTo.DayNumber - from.DayNumber + 1;

        var dated = facts.Select(f => (Fact: f, Date: clock.ToLocalDate(f.CompletedAt))).ToList();
        var perDay = new Dictionary<DateOnly, int>();
        foreach (var (_, date) in dated)
        {
            perDay[date] = perDay.GetValueOrDefault(date) + 1;
        }

        var inPeriod = dated.Where(x => x.Date >= from && x.Date <= to).ToList();
        var (previousFrom, previousTo) = PreviousRange(period, from, to);
        var inPrevious = dated.Where(x => x.Date >= previousFrom && x.Date <= previousTo).ToList();

        var onTime = inPeriod.Count(x => IsOnTime(x.Fact));
        var (current, longest) = Streaks(perDay, today);

        return new StatsReport(
            period,
            from,
            to,
            elapsedDays,
            inPeriod.Count,
            inPrevious.Count,
            inPrevious.Count == 0 ? null : (inPeriod.Count - inPrevious.Count) / (double)inPrevious.Count,
            onTime,
            inPeriod.Count,
            current,
            longest,
            AverageLead(inPeriod.Select(x => x.Fact)),
            Trend(period, from, to, today, perDay),
            ProjectShares(inPeriod.Select(x => x.Fact), projects),
            WeekdayAverages(inPeriod.Select(x => x.Date), from, elapsedTo),
            Heatmap(perDay, today, weekStartsOnMonday))
        {
            PreviousAverageLeadDays = AverageLead(inPrevious.Select(x => x.Fact)),
        };
    }

    private static (DateOnly From, DateOnly To) PeriodRange(StatsPeriod period, DateOnly today, bool weekStartsOnMonday)
    {
        switch (period)
        {
            case StatsPeriod.ThisMonth:
                var first = new DateOnly(today.Year, today.Month, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            case StatsPeriod.ThisYear:
                return (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31));
            default:
                var start = StartOfWeek(today, weekStartsOnMonday);
                return (start, start.AddDays(6));
        }
    }

    private static (DateOnly From, DateOnly To) PreviousRange(StatsPeriod period, DateOnly from, DateOnly to) => period switch
    {
        StatsPeriod.ThisMonth => (from.AddMonths(-1), from.AddDays(-1)),
        StatsPeriod.ThisYear => (from.AddYears(-1), from.AddDays(-1)),
        _ => (from.AddDays(-7), from.AddDays(-1)),
    };

    private static DateOnly StartOfWeek(DateOnly date, bool weekStartsOnMonday) =>
        date.AddDays(-(weekStartsOnMonday ? ((int)date.DayOfWeek + 6) % 7 : (int)date.DayOfWeek));

    private bool IsOnTime(CompletedTaskFact fact)
    {
        if (fact.DueAt is not { } due)
        {
            return true;
        }
        return fact.DueHasTime
            ? fact.CompletedAt <= due
            : clock.ToLocalDate(fact.CompletedAt) <= clock.ToLocalDate(due);
    }

    private static (int Current, int Longest) Streaks(Dictionary<DateOnly, int> perDay, DateOnly today)
    {
        var current = 0;
        // 今日がまだ0件なら昨日から数える（1日の途中で連続が途切れて見えるのを防ぐ）
        var cursor = perDay.ContainsKey(today) ? today : today.AddDays(-1);
        while (perDay.ContainsKey(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        var longest = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var day in perDay.Keys.Order())
        {
            run = previous is { } p && p.AddDays(1) == day ? run + 1 : 1;
            previous = day;
            longest = Math.Max(longest, run);
        }
        return (current, Math.Max(longest, current));
    }

    private static double? AverageLead(IEnumerable<CompletedTaskFact> facts)
    {
        var days = facts.Select(f => Math.Max(0, (f.CompletedAt - f.CreatedAt).TotalDays)).ToList();
        return days.Count == 0 ? null : days.Average();
    }

    private static IReadOnlyList<TrendBar> Trend(StatsPeriod period, DateOnly from, DateOnly to, DateOnly today, Dictionary<DateOnly, int> perDay)
    {
        if (period == StatsPeriod.ThisYear)
        {
            var bars = new List<TrendBar>(12);
            for (var month = 1; month <= 12; month++)
            {
                var start = new DateOnly(from.Year, month, 1);
                var end = start.AddMonths(1).AddDays(-1);
                var count = 0;
                for (var day = start; day <= end; day = day.AddDays(1))
                {
                    count += perDay.GetValueOrDefault(day);
                }
                bars.Add(new TrendBar(start, end, $"{month}月", count, start > today));
            }
            return bars;
        }

        // 今週は「直近14日」、今月はその月の全日
        var first = period == StatsPeriod.ThisWeek ? today.AddDays(-(WeekTrendDays - 1)) : from;
        var last = period == StatsPeriod.ThisWeek ? today : to;
        var daily = new List<TrendBar>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            daily.Add(new TrendBar(day, day, day.Day.ToString(System.Globalization.CultureInfo.InvariantCulture), perDay.GetValueOrDefault(day), day > today));
        }
        return daily;
    }

    private static IReadOnlyList<ProjectShare> ProjectShares(IEnumerable<CompletedTaskFact> facts, IReadOnlyList<Project> projects)
    {
        var byId = new Dictionary<Guid, Project>(projects.Count);
        foreach (var project in projects)
        {
            byId[project.Id] = project;
        }

        var shares = new List<ProjectShare>();
        var unknown = 0;
        foreach (var group in facts.GroupBy(f => f.ProjectId))
        {
            if (group.Key is not { } id)
            {
                shares.Add(new ProjectShare(null, "プロジェクトなし", null, group.Count(), false));
            }
            else if (byId.TryGetValue(id, out var project))
            {
                shares.Add(new ProjectShare(id, project.Name, project.ColorHex, group.Count(), false));
            }
            else
            {
                unknown += group.Count();
            }
        }

        var ordered = shares.OrderByDescending(s => s.Count).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        var top = ordered.Take(4).ToList();
        var rest = ordered.Skip(4).Sum(s => s.Count) + unknown;
        if (rest > 0)
        {
            top.Add(new ProjectShare(null, "その他", null, rest, true));
        }
        return top;
    }

    private static IReadOnlyList<double> WeekdayAverages(IEnumerable<DateOnly> completedDates, DateOnly from, DateOnly elapsedTo)
    {
        var counts = new int[7];
        var days = new int[7];
        for (var day = from; day <= elapsedTo; day = day.AddDays(1))
        {
            days[(int)day.DayOfWeek]++;
        }
        foreach (var date in completedDates)
        {
            counts[(int)date.DayOfWeek]++;
        }
        return [.. Enumerable.Range(0, 7).Select(i => days[i] == 0 ? 0 : counts[i] / (double)days[i])];
    }

    private static IReadOnlyList<HeatCell> Heatmap(Dictionary<DateOnly, int> perDay, DateOnly today, bool weekStartsOnMonday)
    {
        var start = StartOfWeek(today, weekStartsOnMonday).AddDays(-7 * (HeatmapWeeks - 1));
        var counts = new List<(DateOnly Date, int Count)>(HeatmapWeeks * 7);
        for (var day = start; day <= today; day = day.AddDays(1))
        {
            counts.Add((day, perDay.GetValueOrDefault(day)));
        }
        // ponytail: 最大件数に対する比で段階を決める。まとめて完了した日が1日あると他が薄くなる。
        // 気になったら四分位に変える（このメソッドだけで閉じている）
        var max = counts.Count == 0 ? 0 : counts.Max(c => c.Count);
        return
        [
            .. counts.Select(c => new HeatCell(
                c.Date,
                c.Count,
                c.Count == 0 || max == 0 ? 0 : Math.Clamp((int)Math.Ceiling(c.Count * 4.0 / max), 1, 4))),
        ];
    }
}

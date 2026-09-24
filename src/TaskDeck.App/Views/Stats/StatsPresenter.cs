using System.Globalization;
using TaskDeck.Core.Services;

namespace TaskDeck.App.Views.Stats;

/// <summary>前の期間と比べた増減。None は前の期間が0件で比べられないとき。</summary>
public enum ChangeDirection
{
    None,
    Up,
    Down,
    Flat,
}

/// <summary>プロジェクト別の内訳の種類（色の決め方が違う）。</summary>
public enum ShareKind
{
    Project,
    NoProject,
    Other,
}

/// <summary>棒1本。Ratio は軸の上端に対する高さ（0〜1）、Rest はその残り（Grid の * 行に使う）。</summary>
public sealed record StatsBar(string Label, int Count, double Ratio, bool IsFuture, bool IsWeekend, bool IsPeak, string ToolTip)
{
    public double Rest => 1 - Ratio;

    public bool HasValue => Count > 0;
}

/// <summary>曜日別の平均の棒1本。HasPassed は期間のうちその曜日がもう来たか（今週の木曜が未来なら false）。</summary>
public sealed record StatsWeekday(string Label, double Average, double Ratio, bool IsWeekend, bool HasPassed, string ToolTip)
{
    public double Rest => 1 - Ratio;

    public bool HasValue => Average > 0;
}

/// <summary>プロジェクト別の内訳1行。ColorHex はテーマに合わせた色（プロジェクトのときだけ）。</summary>
public sealed record StatsShare(string Name, int Count, string CountText, string PercentText, double Share, string? ColorHex, ShareKind Kind)
{
    public string AutomationName => $"{Name}: {CountText}（{PercentText}）";

    public override string ToString() => AutomationName;
}

/// <summary>ヒートマップの1マス。IsPlaceholder は今週のまだ来ていない日（マスを作らない）。</summary>
public sealed record StatsHeatCell(DateOnly Date, int Count, int Level, bool IsPlaceholder, string ToolTip);

/// <summary>「表で見る」の1行。</summary>
public sealed record StatsTableRow(string Label, string Value)
{
    public override string ToString() => $"{Label}: {Value}";
}

public sealed record StatsTableSection(string Title, IReadOnlyList<StatsTableRow> Rows);

/// <summary>「表で見る」のヒートマップ（1週1行、曜日ごとの件数と合計）。</summary>
public sealed record StatsHeatWeek(string WeekLabel, IReadOnlyList<string> Days, string Total)
{
    public override string ToString() => $"{WeekLabel}: {string.Join("・", Days)}（計 {Total}）";
}

/// <summary>振り返りの画面に出すものすべて（文字列と比率まで作ってあり、画面は並べるだけ）。</summary>
public sealed class StatsDisplay
{
    public required StatsPeriod Period { get; init; }
    public required string PeriodName { get; init; }
    public required string PreviousName { get; init; }
    public required string RangeText { get; init; }

    public required string HeroCaption { get; init; }
    public required string HeroCount { get; init; }
    public required ChangeDirection Change { get; init; }
    public required string ChangeText { get; init; }
    public required string ChangeDescription { get; init; }
    public required string PreviousText { get; init; }

    public required string OnTimePercent { get; init; }
    public required double OnTimeRatio { get; init; }
    public required string OnTimeDetail { get; init; }

    public required string StreakDays { get; init; }
    public required string StreakDetail { get; init; }
    public required IReadOnlyList<StatsBar> StreakBars { get; init; }

    public required string LeadDays { get; init; }
    public required string LeadDetail { get; init; }

    public required string TrendTitle { get; init; }
    public required string TrendCaption { get; init; }
    public required IReadOnlyList<StatsBar> Trend { get; init; }

    /// <summary>軸の数値（上から。最後は 0）。</summary>
    public required IReadOnlyList<string> TrendTicks { get; init; }

    /// <summary>目盛り線を引く数値（0 を除く。上から等間隔）。</summary>
    public IReadOnlyList<string> TrendGridTicks => [.. TrendTicks.Take(TrendTicks.Count - 1)];

    public required string SharesCaption { get; init; }
    public required IReadOnlyList<StatsShare> Shares { get; init; }

    public required IReadOnlyList<StatsWeekday> Weekdays { get; init; }
    public required string WeekdaySummary { get; init; }

    /// <summary>7行×26列を行ごとに並べたもの（UniformGrid の並び）。</summary>
    public required IReadOnlyList<StatsHeatCell> Heatmap { get; init; }

    /// <summary>ヒートマップの行の見出し（7つ。月・水・金の行だけ文字がある）。</summary>
    public required IReadOnlyList<string> HeatRowLabels { get; init; }

    public required bool IsEmpty { get; init; }
    public required string EmptyText { get; init; }

    public required IReadOnlyList<StatsTableSection> Table { get; init; }
    public required IReadOnlyList<string> HeatTableHeader { get; init; }
    public required IReadOnlyList<StatsHeatWeek> HeatTable { get; init; }
}

/// <summary>
/// 振り返りの表示用の変換（S-13、UI 設計書 22章）。StatsCalculator の結果を、画面の文言・棒の高さ・ヒートマップの並びにする。
/// WPF の型を持たない（テストできるように）。色はテーマに合わせたプロジェクト色の16進だけを決め、ブラシは画面が作る。
/// - 比較: 前の期間が0件なら比べない（「先週は 0 件でした」）。増減は四捨五入した百分率に符号を付ける
/// - 推移の軸はきりのいい値（0 / 5 / 10 / 15 のように、目盛りの間は3つまで）。直接ラベルは最大値の1本だけ
/// - ヒートマップは週の始まりの曜日を上にした 7行×26列。今週のまだ来ていない日はマスを作らない
/// </summary>
public static class StatsPresenter
{
    public const int StreakBarDays = 12;

    /// <summary>1件以上ある棒の最低の高さ（軸の上端に対する比）。少ない日でも棒があることが見えるように。</summary>
    public const double MinVisibleRatio = 0.015;

    private static readonly string[] WeekdayNames = ["日", "月", "火", "水", "木", "金", "土"];
    private static readonly int[] NiceSteps = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000];

    public static StatsDisplay Present(StatsReport report, DateOnly today, bool weekStartsOnMonday, bool isDark)
    {
        var (name, previous) = Names(report.Period);
        var trendTop = NiceTop(report.Trend.Count == 0 ? 0 : report.Trend.Max(b => b.Count));
        var heat = HeatLayout(report.Heatmap, weekStartsOnMonday);
        var passed = PassedWeekdays(report.From, report.To, today);

        return new StatsDisplay
        {
            Period = report.Period,
            PeriodName = name,
            PreviousName = previous,
            RangeText = $"{DayText(report.From)} 〜 {DayText(report.To)}",
            HeroCaption = $"{name} 片付けたタスク",
            HeroCount = report.CompletedCount.ToString("N0", CultureInfo.InvariantCulture),
            Change = Direction(report.ChangeRatio),
            ChangeText = ChangeText(report.ChangeRatio),
            ChangeDescription = ChangeDescription(report, previous),
            PreviousText = $"{previous}は {report.PreviousCompletedCount:N0} 件でした",
            OnTimePercent = report.OnTimeDenominator == 0 ? "—" : Percent(report.OnTimeCount, report.OnTimeDenominator),
            OnTimeRatio = report.OnTimeDenominator == 0 ? 0 : report.OnTimeCount / (double)report.OnTimeDenominator,
            OnTimeDetail = report.OnTimeDenominator == 0
                ? "まだ完了がありません"
                : $"{report.OnTimeDenominator:N0} 件中 {report.OnTimeCount:N0} 件",
            StreakDays = report.CurrentStreak.ToString(CultureInfo.InvariantCulture),
            StreakDetail = report.CurrentStreak > 0 && report.CurrentStreak >= report.LongestStreak
                ? "自己最長を更新中です"
                : $"自己最長は {report.LongestStreak} 日",
            StreakBars = StreakBars(report.Heatmap),
            LeadDays = report.AverageLeadDays is { } lead ? Days(lead) : "—",
            LeadDetail = report.PreviousAverageLeadDays is { } previousLead
                ? $"{previous}の平均は {Days(previousLead)} 日"
                : $"{previous}は完了がありません",
            TrendTitle = report.Period == StatsPeriod.ThisYear ? "月ごとに片付けた数" : "日ごとに片付けた数",
            TrendCaption = report.Period switch
            {
                StatsPeriod.ThisYear => $"{report.From.Year}年",
                StatsPeriod.ThisMonth => $"{report.From.Month}月",
                _ => $"直近 {StatsCalculator.WeekTrendDays} 日",
            },
            Trend = TrendBars(report, trendTop.Top),
            TrendTicks = trendTop.Ticks,
            SharesCaption = $"{name}の完了 {report.CompletedCount:N0} 件の内訳",
            Shares = Shares(report, isDark),
            Weekdays = Weekdays(report.WeekdayAverages, passed, weekStartsOnMonday),
            WeekdaySummary = WeekdaySummary(report.WeekdayAverages, passed),
            Heatmap = heat,
            HeatRowLabels = RowLabels(weekStartsOnMonday),
            IsEmpty = report.CompletedCount == 0,
            EmptyText = $"{name}はまだ完了したタスクがありません。終えたタスクがここに積み上がっていきます",
            Table = Table(report, name, previous, today, weekStartsOnMonday),
            HeatTableHeader = [.. Order(weekStartsOnMonday).Select(d => WeekdayNames[d])],
            HeatTable = HeatTable(report.Heatmap),
        };
    }

    /// <summary>軸の上端と目盛り（上から）。目盛りの間は3つまで、値はきりのいい数。0件なら 0〜3。</summary>
    public static (int Top, IReadOnlyList<string> Ticks) NiceTop(int max)
    {
        var target = Math.Max(max, 3);
        var step = NiceSteps.FirstOrDefault(s => (target + s - 1) / s <= 3, NiceSteps[^1]);
        var intervals = Math.Max(1, (target + step - 1) / step);
        var top = step * intervals;
        return (top, [.. Enumerable.Range(0, intervals + 1).Select(i => (top - (i * step)).ToString("N0", CultureInfo.InvariantCulture))]);
    }

    /// <summary>「+18%」「−12%」「±0%」。前の期間が0件なら空。</summary>
    public static string ChangeText(double? ratio) => ratio switch
    {
        null => "",
        { } r when Math.Round(r * 100) == 0 => "±0%",
        { } r when r > 0 => $"+{Math.Round(r * 100):0}%",
        { } r => $"−{Math.Round(-r * 100):0}%",
    };

    private static ChangeDirection Direction(double? ratio) => ratio switch
    {
        null => ChangeDirection.None,
        { } r when Math.Round(r * 100) == 0 => ChangeDirection.Flat,
        { } r when r > 0 => ChangeDirection.Up,
        _ => ChangeDirection.Down,
    };

    private static string ChangeDescription(StatsReport report, string previous) => Direction(report.ChangeRatio) switch
    {
        ChangeDirection.None => $"{previous}は完了がなかったので比べられません",
        ChangeDirection.Flat => $"{previous}と同じです（{report.PreviousCompletedCount:N0} 件）",
        _ => $"{previous}より {ChangeText(report.ChangeRatio)}（{report.PreviousCompletedCount:N0} 件 → {report.CompletedCount:N0} 件）",
    };

    private static (string Name, string Previous) Names(StatsPeriod period) => period switch
    {
        StatsPeriod.ThisMonth => ("今月", "先月"),
        StatsPeriod.ThisYear => ("今年", "去年"),
        _ => ("今週", "先週"),
    };

    private static IReadOnlyList<StatsBar> TrendBars(StatsReport report, int top)
    {
        var bars = report.Trend;
        var peak = bars.Count == 0 ? 0 : bars.Max(b => b.Count);
        var peakIndex = peak > 0 ? bars.ToList().FindIndex(b => b.Count == peak) : -1;
        var sparse = bars.Count > 16;
        var result = new List<StatsBar>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var yearly = report.Period == StatsPeriod.ThisYear;
            // 31本並ぶ月は、1日と5の倍数の日だけ数字を出す（詰まって読めなくなるため）
            var label = sparse && bar.Start.Day != 1 && bar.Start.Day % 5 != 0 ? "" : bar.Label;
            var when = yearly ? $"{bar.Start.Year}年{bar.Start.Month}月" : DayText(bar.Start);
            result.Add(new StatsBar(
                label,
                bar.Count,
                Ratio(bar.Count, top),
                bar.IsFuture,
                !yearly && bar.Start.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                i == peakIndex,
                bar.IsFuture ? $"{when}（まだ先の日）" : $"{when} {bar.Count:N0} 件"));
        }
        return result;
    }

    /// <summary>連続日数のカードの小さな棒（今日までの12日の件数）。</summary>
    private static IReadOnlyList<StatsBar> StreakBars(IReadOnlyList<HeatCell> heatmap)
    {
        var days = heatmap.TakeLast(StreakBarDays).ToList();
        var max = days.Count == 0 ? 0 : days.Max(d => d.Count);
        return
        [
            .. days.Select(d => new StatsBar(
                "",
                d.Count,
                Ratio(d.Count, max),
                false,
                false,
                false,
                $"{DayText(d.Date)} {d.Count:N0} 件")),
        ];
    }

    private static IReadOnlyList<StatsShare> Shares(StatsReport report, bool isDark)
    {
        var total = report.CompletedCount;
        return
        [
            .. report.Projects.Select(p => new StatsShare(
                p.Name,
                p.Count,
                $"{p.Count:N0} 件",
                total == 0 ? "0%" : Percent(p.Count, total) + "%",
                total == 0 ? 0 : p.Count / (double)total,
                p.IsOther || p.ColorHex is null ? null : ProjectPalette.ForTheme(p.ColorHex, isDark),
                p.IsOther ? ShareKind.Other : p.ProjectId is null ? ShareKind.NoProject : ShareKind.Project)),
        ];
    }

    /// <summary>期間のうち今日までに来た曜日（0=日曜〜6=土曜）。今週の木曜が未来なら木曜は false。</summary>
    private static bool[] PassedWeekdays(DateOnly from, DateOnly to, DateOnly today)
    {
        var passed = new bool[7];
        var last = to < today ? to : today;
        for (var day = from; day <= last && !passed.All(p => p); day = day.AddDays(1))
        {
            passed[(int)day.DayOfWeek] = true;
        }
        return passed;
    }

    /// <summary>曜日別（週の始まりの曜日から並べる）。高さは最大の曜日に対する比。まだ来ていない曜日は棒を出さない。</summary>
    private static IReadOnlyList<StatsWeekday> Weekdays(IReadOnlyList<double> averages, bool[] passed, bool weekStartsOnMonday)
    {
        var max = averages.Count == 0 ? 0 : averages.Max();
        return
        [
            .. Order(weekStartsOnMonday).Select(d => new StatsWeekday(
                WeekdayNames[d],
                averages[d],
                max == 0 || averages[d] == 0 ? 0 : Math.Max(MinVisibleRatio, averages[d] / max),
                d is 0 or 6,
                passed[d],
                passed[d] ? $"{WeekdayNames[d]}曜 平均 {Average(averages[d])} 件" : $"{WeekdayNames[d]}曜はこの期間にまだ来ていません")),
        ];
    }

    /// <summary>「水曜がいちばん進んでいます（平均 9.0 件）。日曜は平均 3.2 件。」（もう来た曜日のうち、いちばん少ない曜日を添える）。</summary>
    private static string WeekdaySummary(IReadOnlyList<double> averages, bool[] passed)
    {
        var days = Enumerable.Range(0, 7).Where(d => passed[d]).ToList();
        if (averages.Count != 7 || days.All(d => averages[d] == 0))
        {
            return "この期間はまだ曜日の傾向が出ていません";
        }
        var best = days.MaxBy(d => averages[d]);
        var worst = days.MinBy(d => averages[d]);
        var first = $"{WeekdayNames[best]}曜がいちばん進んでいます（平均 {Average(averages[best])} 件）。";
        return averages[worst] < averages[best] ? first + $"{WeekdayNames[worst]}曜は平均 {Average(averages[worst])} 件。" : first;
    }

    /// <summary>7行×26列を、行ごと（週の始まりの曜日の行から）に並べる。来ていない日は IsPlaceholder。</summary>
    private static IReadOnlyList<StatsHeatCell> HeatLayout(IReadOnlyList<HeatCell> heatmap, bool weekStartsOnMonday)
    {
        var weeks = StatsCalculator.HeatmapWeeks;
        var first = heatmap.Count > 0 ? heatmap[0].Date : default;
        var cells = new List<StatsHeatCell>(weeks * 7);
        for (var row = 0; row < 7; row++)
        {
            for (var column = 0; column < weeks; column++)
            {
                var index = (column * 7) + row;
                cells.Add(index < heatmap.Count
                    ? new StatsHeatCell(heatmap[index].Date, heatmap[index].Count, heatmap[index].Level, false, $"{DayText(heatmap[index].Date)} {heatmap[index].Count:N0} 件")
                    : new StatsHeatCell(first.AddDays(index), 0, 0, true, ""));
            }
        }
        return cells;
    }

    /// <summary>行の見出し（月・水・金だけ）。</summary>
    private static IReadOnlyList<string> RowLabels(bool weekStartsOnMonday) =>
        [.. Order(weekStartsOnMonday).Select(d => d is 1 or 3 or 5 ? WeekdayNames[d] : "")];

    private static IReadOnlyList<StatsHeatWeek> HeatTable(IReadOnlyList<HeatCell> heatmap)
    {
        var weeks = new List<StatsHeatWeek>();
        for (var start = 0; start < heatmap.Count; start += 7)
        {
            var week = heatmap.Skip(start).Take(7).ToList();
            var days = Enumerable.Range(0, 7).Select(i => i < week.Count ? week[i].Count.ToString("N0", CultureInfo.InvariantCulture) : "").ToList();
            weeks.Add(new StatsHeatWeek($"{week[0].Date.Month}月{week[0].Date.Day}日の週", days, week.Sum(c => c.Count).ToString("N0", CultureInfo.InvariantCulture)));
        }
        weeks.Reverse();   // 新しい週を上に
        return weeks;
    }

    private static IReadOnlyList<StatsTableSection> Table(StatsReport report, string name, string previous, DateOnly today, bool weekStartsOnMonday)
    {
        var summary = new List<StatsTableRow>
        {
            new("期間", $"{DayText(report.From)} 〜 {DayText(report.To)}"),
            new($"{name} 片付けたタスク", $"{report.CompletedCount:N0} 件"),
            new($"{previous} 片付けたタスク", $"{report.PreviousCompletedCount:N0} 件"),
            new($"{previous}との比較", report.ChangeRatio is null ? "比べられません（前の期間が0件）" : ChangeText(report.ChangeRatio)),
            new(
                "期限内に終えた割合",
                report.OnTimeDenominator == 0
                    ? "—"
                    : $"{Percent(report.OnTimeCount, report.OnTimeDenominator)}%（{report.OnTimeDenominator:N0} 件中 {report.OnTimeCount:N0} 件）"),
            new("連続で片付けた日数", $"{report.CurrentStreak} 日"),
            new("連続の自己最長", $"{report.LongestStreak} 日"),
            new("作ってから終えるまでの平均", report.AverageLeadDays is { } lead ? $"{Days(lead)} 日" : "—"),
            new($"{previous}の平均", report.PreviousAverageLeadDays is { } previousLead ? $"{Days(previousLead)} 日" : "—"),
        };

        var yearly = report.Period == StatsPeriod.ThisYear;
        var trend = report.Trend
            .Where(b => !b.IsFuture)
            .Select(b => new StatsTableRow(yearly ? $"{b.Start.Year}年{b.Start.Month}月" : DayText(b.Start), $"{b.Count:N0} 件"))
            .ToList();

        var total = report.CompletedCount;
        var shares = report.Projects
            .Select(p => new StatsTableRow(p.Name, $"{p.Count:N0} 件（{(total == 0 ? "0" : Percent(p.Count, total))}%）"))
            .ToList();
        if (shares.Count == 0)
        {
            shares.Add(new StatsTableRow("—", "完了がありません"));
        }

        var passed = PassedWeekdays(report.From, report.To, today);
        var weekdays = Order(weekStartsOnMonday)
            .Select(d => new StatsTableRow(
                $"{WeekdayNames[d]}曜",
                passed[d] ? $"{Average(report.WeekdayAverages[d])} 件" : "—（この期間にまだ来ていない）"))
            .ToList();

        return
        [
            new StatsTableSection("まとめ", summary),
            new StatsTableSection(yearly ? "月ごとに片付けた数" : "日ごとに片付けた数", trend),
            new StatsTableSection("プロジェクト別の内訳", shares),
            new StatsTableSection("曜日ごとの1日あたりの平均", weekdays),
        ];
    }

    /// <summary>曜日の並び（0=日曜〜6=土曜 の番号を、週の始まりから）。</summary>
    private static IEnumerable<int> Order(bool weekStartsOnMonday) =>
        weekStartsOnMonday ? [1, 2, 3, 4, 5, 6, 0] : [0, 1, 2, 3, 4, 5, 6];

    /// <summary>件数の高さの比（0件は0、1件以上は最低 MinVisibleRatio）。</summary>
    private static double Ratio(int count, int top) =>
        count <= 0 || top <= 0 ? 0 : Math.Min(1, Math.Max(MinVisibleRatio, count / (double)top));

    private static string DayText(DateOnly date) =>
        $"{date.Month}月{date.Day}日（{WeekdayNames[(int)date.DayOfWeek]}）";

    private static string Percent(int part, int whole) =>
        Math.Round(part * 100.0 / whole, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string Days(double days) => days.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Average(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

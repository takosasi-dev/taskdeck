using TaskDeck.App.Views.Stats;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Insights;

/// <summary>
/// 振り返りの表示用の変換（期間の名前・比較の文言・軸・ヒートマップの並びと段階・曜日・表）。
/// 時計は JST 2026-09-23（水）10:00。週は日曜始まり（今週は 9/20〜9/26、先週は 9/13〜9/19）。
/// </summary>
public sealed class StatsPresenterTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 23, 10, 0);
    private static readonly DateOnly Today = new(2026, 9, 23);
    private static readonly StatsCalculator Calculator = new(Clock);
    private static readonly Project Work = new() { Name = "業務改善", ColorHex = "#0067C0" };

    [Fact]
    public void Present_MoreThanLastWeek_ShowsPlusPercentWithCounts()
    {
        var display = Present([.. Days(9, 20, 6), .. Days(9, 14, 5)], StatsPeriod.ThisWeek);

        Assert.Equal("今週 片付けたタスク", display.HeroCaption);
        Assert.Equal("6", display.HeroCount);
        Assert.Equal(ChangeDirection.Up, display.Change);
        Assert.Equal("+20%", display.ChangeText);
        Assert.Equal("先週より +20%（5 件 → 6 件）", display.ChangeDescription);
        Assert.Equal("先週は 5 件でした", display.PreviousText);
    }

    [Fact]
    public void Present_FewerThanLastWeek_ShowsMinusPercent()
    {
        var display = Present([.. Days(9, 21, 3), .. Days(9, 15, 4)], StatsPeriod.ThisWeek);

        Assert.Equal(ChangeDirection.Down, display.Change);
        Assert.Equal("−25%", display.ChangeText);
    }

    [Fact]
    public void Present_SameAsLastWeek_ShowsNoChange()
    {
        var display = Present([.. Days(9, 21, 4), .. Days(9, 15, 4)], StatsPeriod.ThisWeek);

        Assert.Equal(ChangeDirection.Flat, display.Change);
        Assert.Equal("±0%", display.ChangeText);
        Assert.Equal("先週と同じです（4 件）", display.ChangeDescription);
    }

    [Fact]
    public void Present_NothingLastWeek_DoesNotCompare()
    {
        var display = Present(Days(9, 21, 2), StatsPeriod.ThisWeek);

        Assert.Equal(ChangeDirection.None, display.Change);
        Assert.Equal("", display.ChangeText);
        Assert.Equal("先週は完了がなかったので比べられません", display.ChangeDescription);
        Assert.Equal("先週は 0 件でした", display.PreviousText);
    }

    [Fact]
    public void Present_MonthAndYear_UsePeriodNames()
    {
        var facts = Days(9, 21, 2);

        var month = Present(facts, StatsPeriod.ThisMonth);
        Assert.Equal("今月 片付けたタスク", month.HeroCaption);
        Assert.Equal("先月は 0 件でした", month.PreviousText);
        Assert.Equal("日ごとに片付けた数", month.TrendTitle);
        Assert.Equal("9月", month.TrendCaption);
        Assert.Equal(30, month.Trend.Count);

        var year = Present(facts, StatsPeriod.ThisYear);
        Assert.Equal("今年 片付けたタスク", year.HeroCaption);
        Assert.Equal("去年は 0 件でした", year.PreviousText);
        Assert.Equal("月ごとに片付けた数", year.TrendTitle);
        Assert.Equal("2026年", year.TrendCaption);
        Assert.Equal(12, year.Trend.Count);
        Assert.Equal("9月", year.Trend[8].Label);
    }

    [Theory]
    [InlineData(0, 3, new[] { "3", "2", "1", "0" })]
    [InlineData(3, 3, new[] { "3", "2", "1", "0" })]
    [InlineData(4, 4, new[] { "4", "2", "0" })]
    [InlineData(14, 15, new[] { "15", "10", "5", "0" })]
    [InlineData(38, 40, new[] { "40", "20", "0" })]
    [InlineData(842, 1000, new[] { "1,000", "500", "0" })]
    public void NiceTop_Max_ReturnsRoundTicks(int max, int top, string[] ticks)
    {
        var (actualTop, actualTicks) = StatsPresenter.NiceTop(max);

        Assert.Equal(top, actualTop);
        Assert.Equal(ticks, actualTicks);
    }

    [Fact]
    public void Present_Trend_MarksOnlyFirstPeakAndKeepsSmallBarsVisible()
    {
        // 9/21 に 1件、9/22 と 9/23 に 100件ずつ
        var display = Present([.. Days(9, 21, 1), .. Days(9, 22, 100), .. Days(9, 23, 100)], StatsPeriod.ThisWeek);

        var peaks = display.Trend.Where(b => b.IsPeak).ToList();
        var peak = Assert.Single(peaks);
        Assert.Equal("22", peak.Label);
        Assert.Equal(1.0, peak.Ratio);
        var small = display.Trend.Single(b => b.Label == "21");
        Assert.Equal(StatsPresenter.MinVisibleRatio, small.Ratio);   // 1/100 は細すぎるので最低の高さ
        Assert.Equal(0, display.Trend.Single(b => b.Label == "20").Ratio);
        Assert.Equal(["100", "50", "0"], display.TrendTicks);
        Assert.Equal(["100", "50"], display.TrendGridTicks);
    }

    [Fact]
    public void Present_Month_ShowsDayNumbersSparsely()
    {
        var display = Present(Days(9, 21, 1), StatsPeriod.ThisMonth);

        Assert.Equal("1", display.Trend[0].Label);
        Assert.Equal("", display.Trend[1].Label);
        Assert.Equal("5", display.Trend[4].Label);
        Assert.True(display.Trend[29].IsFuture);
        Assert.Equal("9月30日（水）（まだ先の日）", display.Trend[29].ToolTip);
        Assert.True(display.Trend[5].IsWeekend);   // 9/6 は日曜
    }

    [Fact]
    public void Present_Heatmap_LaysOutSevenRowsFromWeekStartAndHidesFutureDays()
    {
        var display = Present(Days(9, 20, 1), StatsPeriod.ThisWeek);

        Assert.Equal(7 * StatsCalculator.HeatmapWeeks, display.Heatmap.Count);
        // 行ごとの並び: 1行目は日曜。最後の列（今週）の日曜は 9/20
        var sundayThisWeek = display.Heatmap[StatsCalculator.HeatmapWeeks - 1];
        Assert.Equal(new DateOnly(2026, 9, 20), sundayThisWeek.Date);
        Assert.Equal(4, sundayThisWeek.Level);
        Assert.Equal("9月20日（日） 1 件", sundayThisWeek.ToolTip);
        // 水曜（4行目）の最後の列は今日、木曜（5行目）の最後の列はまだ来ていない
        var wednesday = display.Heatmap[(3 * StatsCalculator.HeatmapWeeks) + StatsCalculator.HeatmapWeeks - 1];
        Assert.Equal(Today, wednesday.Date);
        Assert.False(wednesday.IsPlaceholder);
        var thursday = display.Heatmap[(4 * StatsCalculator.HeatmapWeeks) + StatsCalculator.HeatmapWeeks - 1];
        Assert.True(thursday.IsPlaceholder);
        Assert.Equal(3, display.Heatmap.Count(c => c.IsPlaceholder));
        Assert.Equal(["", "月", "", "水", "", "金", ""], display.HeatRowLabels);
    }

    [Fact]
    public void Present_HeatmapMondayStart_PutsMondayOnTop()
    {
        var display = StatsPresenter.Present(Calculate(Days(9, 21, 1), StatsPeriod.ThisWeek, monday: true), Today, weekStartsOnMonday: true, isDark: false);

        Assert.Equal(DayOfWeek.Monday, display.Heatmap[0].Date.DayOfWeek);
        Assert.Equal(["月", "", "水", "", "金", "", ""], display.HeatRowLabels);
        Assert.Equal(["月", "火", "水", "木", "金", "土", "日"], display.Weekdays.Select(w => w.Label));
        Assert.Equal(["月", "火", "水", "木", "金", "土", "日"], display.HeatTableHeader);
    }

    [Fact]
    public void Present_HeatLevels_FollowReportLevels()
    {
        // 最大4件に対して 1〜4件 → 段階 1〜4
        var display = Present([.. Days(9, 20, 1), .. Days(9, 21, 2), .. Days(9, 22, 3), .. Days(9, 23, 4)], StatsPeriod.ThisWeek);

        var lastColumn = StatsCalculator.HeatmapWeeks - 1;
        Assert.Equal([1, 2, 3, 4], Enumerable.Range(0, 4).Select(row => display.Heatmap[(row * StatsCalculator.HeatmapWeeks) + lastColumn].Level));
        Assert.Equal(0, display.Heatmap[lastColumn - 1].Level);   // 先週の日曜は0件
    }

    [Fact]
    public void Present_WeekdaysNotYetCome_AreLeftOutOfSummary()
    {
        // 今週: 日曜2件・月曜1件（火・水は0件、木〜土はまだ来ていない）
        var display = Present([.. Days(9, 20, 2), .. Days(9, 21, 1)], StatsPeriod.ThisWeek);

        Assert.Equal("日曜がいちばん進んでいます（平均 2.0 件）。火曜は平均 0.0 件。", display.WeekdaySummary);
        var thursday = display.Weekdays[4];
        Assert.False(thursday.HasPassed);
        Assert.Equal("木曜はこの期間にまだ来ていません", thursday.ToolTip);
        Assert.True(display.Weekdays[0].IsWeekend);
        Assert.Equal(1.0, display.Weekdays[0].Ratio);
    }

    [Fact]
    public void Present_NoCompletions_ShowsEmptyState()
    {
        var display = Present([], StatsPeriod.ThisWeek);

        Assert.True(display.IsEmpty);
        Assert.Equal("0", display.HeroCount);
        Assert.Equal("—", display.OnTimePercent);
        Assert.Equal("まだ完了がありません", display.OnTimeDetail);
        Assert.Equal("—", display.LeadDays);
        Assert.Empty(display.Shares);
        Assert.Equal("この期間はまだ曜日の傾向が出ていません", display.WeekdaySummary);
        Assert.Equal(["3", "2", "1", "0"], display.TrendTicks);
        Assert.StartsWith("今週はまだ完了したタスクがありません", display.EmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Present_OnTimeAndStreakAndLead_Formatted()
    {
        // 期限を過ぎて終えた1件（期限 9/20・完了 9/21）と、期限なしの3件。作ってから 2 日後に完了
        var late = Fact(9, 21, due: FixedClock.LocalToUtc(2026, 9, 20), createdDaysBefore: 2);
        var display = Present([late, .. Days(9, 21, 1), .. Days(9, 22, 2)], StatsPeriod.ThisWeek);

        Assert.Equal("75", display.OnTimePercent);
        Assert.Equal(0.75, display.OnTimeRatio);
        Assert.Equal("4 件中 3 件", display.OnTimeDetail);
        Assert.Equal("2", display.StreakDays);   // 今日（9/23）はまだ0件なので昨日から数える
        Assert.Equal("自己最長を更新中です", display.StreakDetail);
        Assert.Equal(12, display.StreakBars.Count);
        Assert.Equal("1.3", display.LeadDays);   // (2 + 1 + 1 + 1) / 4 = 1.25 → 小数1桁
        Assert.Equal("先週は完了がありません", display.LeadDetail);
    }

    [Fact]
    public void Present_Shares_UseThemeColorsAndKinds()
    {
        var facts = new List<CompletedTaskFact>
        {
            Fact(9, 21, project: Work.Id),
            Fact(9, 21, project: Work.Id),
            Fact(9, 22),
        };

        var light = Present(facts, StatsPeriod.ThisWeek);
        var dark = StatsPresenter.Present(Calculate(facts, StatsPeriod.ThisWeek), Today, weekStartsOnMonday: false, isDark: true);

        Assert.Equal("今週の完了 3 件の内訳", light.SharesCaption);
        Assert.Equal(("業務改善", "2 件", "67%", "#0067C0", ShareKind.Project), Tuple(light.Shares[0]));
        Assert.Equal(("プロジェクトなし", "1 件", "33%", null, ShareKind.NoProject), Tuple(light.Shares[1]));
        Assert.Equal(ProjectPalette.Dark[0], dark.Shares[0].ColorHex);
    }

    [Fact]
    public void Present_Table_HasEveryNumber()
    {
        var display = Present([.. Days(9, 20, 6), .. Days(9, 14, 5)], StatsPeriod.ThisWeek);

        var summary = display.Table[0];
        Assert.Equal("まとめ", summary.Title);
        Assert.Equal(
            [
                "期間: 9月20日（日） 〜 9月26日（土）",
                "今週 片付けたタスク: 6 件",
                "先週 片付けたタスク: 5 件",
                "先週との比較: +20%",
                "期限内に終えた割合: 100%（6 件中 6 件）",
                "連続で片付けた日数: 0 日",
                "連続の自己最長: 1 日",
                "作ってから終えるまでの平均: 1.0 日",
                "先週の平均: 1.0 日",
            ],
            summary.Rows.Select(r => r.ToString()));
        Assert.Equal("日ごとに片付けた数", display.Table[1].Title);
        Assert.Equal(StatsCalculator.WeekTrendDays, display.Table[1].Rows.Count);   // 直近14日（9/10〜今日）
        Assert.Contains(new StatsTableRow("9月20日（日）", "6 件"), display.Table[1].Rows);
        Assert.Equal("プロジェクト別の内訳", display.Table[2].Title);
        Assert.Equal("曜日ごとの1日あたりの平均", display.Table[3].Title);
        Assert.Contains(new StatsTableRow("木曜", "—（この期間にまだ来ていない）"), display.Table[3].Rows);
        Assert.Equal(StatsCalculator.HeatmapWeeks, display.HeatTable.Count);
        Assert.Equal("9月20日の週", display.HeatTable[0].WeekLabel);   // 新しい週が上
        Assert.Equal(["6", "0", "0", "0", "", "", ""], display.HeatTable[0].Days);
    }

    private static (string, string, string, string?, ShareKind) Tuple(StatsShare share) =>
        (share.Name, share.CountText, share.PercentText, share.ColorHex, share.Kind);

    private static StatsDisplay Present(IReadOnlyList<CompletedTaskFact> facts, StatsPeriod period) =>
        StatsPresenter.Present(Calculate(facts, period), Today, weekStartsOnMonday: false, isDark: false);

    private static StatsReport Calculate(IReadOnlyList<CompletedTaskFact> facts, StatsPeriod period, bool monday = false) =>
        Calculator.Calculate(facts, period, [Work], monday);

    /// <summary>その日（JST）の昼に完了した count 件。作ったのは前日。</summary>
    private static IReadOnlyList<CompletedTaskFact> Days(int month, int day, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => Fact(month, day))];

    private static CompletedTaskFact Fact(int month, int day, DateTime? due = null, Guid? project = null, int createdDaysBefore = 1)
    {
        var completed = FixedClock.LocalToUtc(2026, month, day, 12, 0);
        return new CompletedTaskFact(Guid.NewGuid(), completed, completed.AddDays(-createdDaysBefore), due, false, project);
    }
}

using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Core;

/// <summary>設計書 4.6 の指標と境界条件。時計は JST 2026-09-22（火）10:00。</summary>
public class StatsCalculatorTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
    private static readonly StatsCalculator Calculator = new(Clock);

    private static CompletedTaskFact Fact(
        DateTime completedUtc,
        DateTime? createdUtc = null,
        DateTime? dueUtc = null,
        bool dueHasTime = false,
        Guid? projectId = null) =>
        new(Guid.CreateVersion7(), completedUtc, createdUtc ?? completedUtc, dueUtc, dueHasTime, projectId);

    private static CompletedTaskFact OnDay(int year, int month, int day, int hour = 12, Guid? projectId = null) =>
        Fact(FixedClock.LocalToUtc(year, month, day, hour), projectId: projectId);

    private static StatsReport Calculate(
        IReadOnlyList<CompletedTaskFact> facts,
        StatsPeriod period = StatsPeriod.ThisWeek,
        IReadOnlyList<Project>? projects = null,
        bool weekStartsOnMonday = false) =>
        Calculator.Calculate(facts, period, projects ?? [], weekStartsOnMonday);

    [Fact]
    public void Calculate_完了0件_すべて0で軸だけ残る()
    {
        var report = Calculate([]);

        Assert.Equal(0, report.CompletedCount);
        Assert.Equal(0, report.PreviousCompletedCount);
        Assert.Null(report.ChangeRatio);
        Assert.Null(report.AverageLeadDays);
        Assert.Equal(0, report.CurrentStreak);
        Assert.Equal(0, report.LongestStreak);
        Assert.Empty(report.Projects);
        Assert.Equal(StatsCalculator.WeekTrendDays, report.Trend.Count);
        Assert.All(report.Trend, bar => Assert.Equal(0, bar.Count));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0], report.WeekdayAverages);
        Assert.All(report.Heatmap, cell => Assert.Equal(0, cell.Level));
    }

    [Theory]
    [InlineData(StatsPeriod.ThisWeek, false, "2026-09-20", "2026-09-26", 3)]
    [InlineData(StatsPeriod.ThisWeek, true, "2026-09-21", "2026-09-27", 2)]
    [InlineData(StatsPeriod.ThisMonth, false, "2026-09-01", "2026-09-30", 22)]
    [InlineData(StatsPeriod.ThisYear, false, "2026-01-01", "2026-12-31", 265)]
    public void Calculate_期間と経過日数(StatsPeriod period, bool mondayStart, string from, string to, int elapsed)
    {
        var report = Calculate([], period, weekStartsOnMonday: mondayStart);

        Assert.Equal(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), report.From);
        Assert.Equal(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), report.To);
        Assert.Equal(elapsed, report.ElapsedDays);
    }

    [Fact]
    public void Calculate_深夜の完了は翌日として数える()
    {
        // UTC 9/22 15:30 は JST 9/23 0:30
        var report = Calculate([Fact(new DateTime(2026, 9, 22, 15, 30, 0, DateTimeKind.Utc))], StatsPeriod.ThisMonth);

        Assert.Equal(0, report.Trend.Single(b => b.Start == new DateOnly(2026, 9, 22)).Count);
        Assert.Equal(1, report.Trend.Single(b => b.Start == new DateOnly(2026, 9, 23)).Count);
    }

    [Fact]
    public void Calculate_期間の外の完了は数えない()
    {
        var report = Calculate([OnDay(2026, 9, 19), OnDay(2026, 9, 21)], StatsPeriod.ThisWeek, weekStartsOnMonday: true);

        Assert.Equal(1, report.CompletedCount);
        Assert.Equal(1, report.PreviousCompletedCount);
    }

    [Fact]
    public void Calculate_前期間比_先週との増減()
    {
        // 今週（日曜始まり 9/20〜）3件、先週（9/13〜9/19）2件
        var facts = new[] { OnDay(2026, 9, 20), OnDay(2026, 9, 21), OnDay(2026, 9, 22), OnDay(2026, 9, 15), OnDay(2026, 9, 16) };

        var report = Calculate(facts);

        Assert.Equal(3, report.CompletedCount);
        Assert.Equal(2, report.PreviousCompletedCount);
        Assert.Equal(0.5, report.ChangeRatio);
    }

    [Fact]
    public void Calculate_前期間が0件_比較はnull()
    {
        Assert.Null(Calculate([OnDay(2026, 9, 22)]).ChangeRatio);
    }

    [Fact]
    public void Calculate_期限内完了_日付のみの期限はその日のうちなら間に合い()
    {
        var due = Clock.LocalDayStartUtc(new DateOnly(2026, 9, 21));
        var facts = new[]
        {
            Fact(FixedClock.LocalToUtc(2026, 9, 21, 23, 50), dueUtc: due),        // 当日の深夜 → 期限内
            Fact(FixedClock.LocalToUtc(2026, 9, 22, 0, 30), dueUtc: due),         // 翌日 → 遅れ
            Fact(FixedClock.LocalToUtc(2026, 9, 22, 9, 0)),                        // 期限なし → 期限内
        };

        var report = Calculate(facts);

        Assert.Equal(3, report.OnTimeDenominator);
        Assert.Equal(2, report.OnTimeCount);
    }

    [Fact]
    public void Calculate_期限内完了_時刻ありはその時刻まで()
    {
        var due = FixedClock.LocalToUtc(2026, 9, 21, 15, 0);
        var facts = new[]
        {
            Fact(FixedClock.LocalToUtc(2026, 9, 21, 15, 0), dueUtc: due, dueHasTime: true),
            Fact(FixedClock.LocalToUtc(2026, 9, 21, 15, 1), dueUtc: due, dueHasTime: true),
        };

        var report = Calculate(facts);

        Assert.Equal(1, report.OnTimeCount);
    }

    [Fact]
    public void Calculate_連続日数_今日がまだ0件でも昨日から数える()
    {
        var report = Calculate([OnDay(2026, 9, 19), OnDay(2026, 9, 20), OnDay(2026, 9, 21)]);

        Assert.Equal(3, report.CurrentStreak);
    }

    [Fact]
    public void Calculate_連続日数_今日を含めて数える()
    {
        var report = Calculate([OnDay(2026, 9, 20), OnDay(2026, 9, 21), OnDay(2026, 9, 22)]);

        Assert.Equal(3, report.CurrentStreak);
    }

    [Fact]
    public void Calculate_連続日数_一昨日までで途切れていたら0()
    {
        var report = Calculate([OnDay(2026, 9, 18), OnDay(2026, 9, 19), OnDay(2026, 9, 20)]);

        Assert.Equal(0, report.CurrentStreak);
        Assert.Equal(3, report.LongestStreak);
    }

    [Fact]
    public void Calculate_自己最長は全期間から探す()
    {
        var facts = new[]
        {
            OnDay(2026, 5, 1), OnDay(2026, 5, 2), OnDay(2026, 5, 3), OnDay(2026, 5, 4), OnDay(2026, 5, 5),
            OnDay(2026, 9, 21), OnDay(2026, 9, 22),
        };

        var report = Calculate(facts);

        Assert.Equal(2, report.CurrentStreak);
        Assert.Equal(5, report.LongestStreak);
    }

    [Fact]
    public void Calculate_平均リードタイム_作成から完了までの日数()
    {
        var facts = new[]
        {
            Fact(FixedClock.LocalToUtc(2026, 9, 22, 10, 0), FixedClock.LocalToUtc(2026, 9, 18, 10, 0)),
            Fact(FixedClock.LocalToUtc(2026, 9, 22, 10, 0), FixedClock.LocalToUtc(2026, 9, 20, 10, 0)),
        };

        Assert.Equal(3.0, Calculate(facts).AverageLeadDays);
    }

    [Fact]
    public void Calculate_作成日時が完了より後の壊れたデータ_0日として数える()
    {
        var facts = new[] { Fact(FixedClock.LocalToUtc(2026, 9, 22, 10, 0), FixedClock.LocalToUtc(2026, 9, 25, 10, 0)) };

        Assert.Equal(0.0, Calculate(facts).AverageLeadDays);
    }

    [Fact]
    public void Calculate_前期間の平均リードタイムも返す()
    {
        var facts = new[]
        {
            Fact(FixedClock.LocalToUtc(2026, 9, 22, 10, 0), FixedClock.LocalToUtc(2026, 9, 21, 10, 0)),
            Fact(FixedClock.LocalToUtc(2026, 9, 16, 10, 0), FixedClock.LocalToUtc(2026, 9, 14, 10, 0)),
        };

        var report = Calculate(facts);

        Assert.Equal(1.0, report.AverageLeadDays);
        Assert.Equal(2.0, report.PreviousAverageLeadDays);
    }

    [Fact]
    public void Calculate_プロジェクト別_上位4件とその他()
    {
        var projects = Enumerable.Range(0, 6)
            .Select(i => new Project { Id = Guid.CreateVersion7(), Name = $"P{i}", ColorHex = "#0067C0" })
            .ToList();
        var counts = new[] { 6, 5, 4, 3, 2, 1 };
        var facts = projects
            .SelectMany((p, i) => Enumerable.Range(0, counts[i]).Select(_ => OnDay(2026, 9, 22, projectId: p.Id)))
            .ToList();

        var report = Calculate(facts, projects: projects);

        Assert.Equal(["P0", "P1", "P2", "P3", "その他"], report.Projects.Select(p => p.Name));
        Assert.Equal([6, 5, 4, 3, 3], report.Projects.Select(p => p.Count));
        Assert.True(report.Projects[^1].IsOther);
        Assert.Equal("#0067C0", report.Projects[0].ColorHex);
    }

    [Fact]
    public void Calculate_プロジェクトなしはひとつの内訳として並ぶ()
    {
        var project = new Project { Id = Guid.CreateVersion7(), Name = "業務改善" };
        var facts = new[] { OnDay(2026, 9, 22, projectId: project.Id), OnDay(2026, 9, 22), OnDay(2026, 9, 22) };

        var report = Calculate(facts, projects: [project]);

        Assert.Equal(["プロジェクトなし", "業務改善"], report.Projects.Select(p => p.Name));
        Assert.Equal([2, 1], report.Projects.Select(p => p.Count));
        Assert.All(report.Projects, p => Assert.False(p.IsOther));
    }

    [Fact]
    public void Calculate_名前の分からないプロジェクトはその他に寄せる()
    {
        var report = Calculate([OnDay(2026, 9, 22, projectId: Guid.CreateVersion7())], projects: []);

        var other = Assert.Single(report.Projects);
        Assert.Equal("その他", other.Name);
        Assert.True(other.IsOther);
        Assert.Equal(1, other.Count);
    }

    [Fact]
    public void Calculate_曜日別_過ぎた日数で割る()
    {
        // 月曜始まりの今週（9/21 月・9/22 火まで経過）。月曜2件・火曜1件
        var facts = new[] { OnDay(2026, 9, 21), OnDay(2026, 9, 21), OnDay(2026, 9, 22) };

        var report = Calculate(facts, StatsPeriod.ThisWeek, weekStartsOnMonday: true);

        Assert.Equal(2.0, report.WeekdayAverages[(int)DayOfWeek.Monday]);
        Assert.Equal(1.0, report.WeekdayAverages[(int)DayOfWeek.Tuesday]);
        Assert.Equal(0.0, report.WeekdayAverages[(int)DayOfWeek.Wednesday]);
    }

    [Fact]
    public void Calculate_推移_今週は直近14日で未来を含まない()
    {
        var report = Calculate([OnDay(2026, 9, 9), OnDay(2026, 9, 22)]);

        Assert.Equal(14, report.Trend.Count);
        Assert.Equal(new DateOnly(2026, 9, 9), report.Trend[0].Start);
        Assert.Equal(new DateOnly(2026, 9, 22), report.Trend[^1].Start);
        Assert.Equal(1, report.Trend[0].Count);
        Assert.Equal(1, report.Trend[^1].Count);
        Assert.DoesNotContain(report.Trend, bar => bar.IsFuture);
    }

    [Fact]
    public void Calculate_推移_今月は月末まで伸ばして未来は0()
    {
        var report = Calculate([], StatsPeriod.ThisMonth);

        Assert.Equal(30, report.Trend.Count);
        Assert.Equal(new DateOnly(2026, 9, 1), report.Trend[0].Start);
        Assert.Equal(new DateOnly(2026, 9, 30), report.Trend[^1].Start);
        Assert.False(report.Trend[21].IsFuture);
        Assert.True(report.Trend[22].IsFuture);
        Assert.Equal(8, report.Trend.Count(bar => bar.IsFuture));
    }

    [Fact]
    public void Calculate_推移_今年は月ごと12本()
    {
        var report = Calculate([OnDay(2026, 1, 5), OnDay(2026, 9, 22)], StatsPeriod.ThisYear);

        Assert.Equal(12, report.Trend.Count);
        Assert.Equal(["1月", "2月", "3月", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月"], report.Trend.Select(b => b.Label));
        Assert.Equal(1, report.Trend[0].Count);
        Assert.Equal(1, report.Trend[8].Count);
        Assert.Equal(3, report.Trend.Count(b => b.IsFuture));
        Assert.Equal(new DateOnly(2026, 2, 28), report.Trend[1].End);
    }

    [Fact]
    public void Calculate_ヒートマップ_週の開始にそろえて今日まで()
    {
        var mondayStart = Calculate([], weekStartsOnMonday: true).Heatmap;
        var sundayStart = Calculate([], weekStartsOnMonday: false).Heatmap;

        Assert.Equal(new DateOnly(2026, 9, 21).AddDays(-7 * (StatsCalculator.HeatmapWeeks - 1)), mondayStart[0].Date);
        Assert.Equal(DayOfWeek.Monday, mondayStart[0].Date.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, sundayStart[0].Date.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 9, 22), mondayStart[^1].Date);
        Assert.Equal(177, mondayStart.Count);
        Assert.Equal(178, sundayStart.Count);
    }

    [Fact]
    public void Calculate_ヒートマップ_段階は最大件数に対する比()
    {
        var facts = new List<CompletedTaskFact>();
        facts.AddRange(Enumerable.Range(0, 4).Select(_ => OnDay(2026, 9, 22)));
        facts.AddRange(Enumerable.Range(0, 2).Select(_ => OnDay(2026, 9, 21)));
        facts.Add(OnDay(2026, 9, 20));

        var heatmap = Calculate(facts).Heatmap.ToDictionary(c => c.Date);

        Assert.Equal(4, heatmap[new DateOnly(2026, 9, 22)].Level);
        Assert.Equal(2, heatmap[new DateOnly(2026, 9, 21)].Level);
        Assert.Equal(1, heatmap[new DateOnly(2026, 9, 20)].Level);
        Assert.Equal(0, heatmap[new DateOnly(2026, 9, 19)].Level);
        Assert.Equal(4, heatmap[new DateOnly(2026, 9, 22)].Count);
    }

    [Fact]
    public void Calculate_ヒートマップの外の古い完了は自己最長にだけ効く()
    {
        var old = new[] { OnDay(2025, 1, 1), OnDay(2025, 1, 2), OnDay(2025, 1, 3) };

        var report = Calculate(old);

        Assert.Equal(0, report.CompletedCount);
        Assert.Equal(3, report.LongestStreak);
        Assert.DoesNotContain(report.Heatmap, cell => cell.Count > 0);
    }
}

using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Core;

/// <summary>設計書 4.1 の境界条件表。時計は JST 固定、RRULE はローカル時刻で評価される。</summary>
public class RecurrenceEngineTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private static RecurrenceEngine Engine(bool shiftOnHolidays = false, params DateOnly[] holidays)
    {
        var settings = new FakeSettingsStore();
        settings.Update(s => s.External.ShiftWeekdayRecurrenceOnHolidays = shiftOnHolidays);
        return new RecurrenceEngine(Clock, new FixedHolidays(holidays), settings);
    }

    private static TaskItem DateOnlyTask(int year, int month, int day) =>
        new() { DueAt = Clock.LocalDayStartUtc(new DateOnly(year, month, day)), DueHasTime = false };

    private static TaskItem TimedTask(DateTime dueUtc) => new() { DueAt = dueUtc, DueHasTime = true };

    private static RecurrenceRule Rule(
        string rrule,
        RecurrenceBaseKind baseKind = RecurrenceBaseKind.DueDate,
        RecurrenceEndKind endKind = RecurrenceEndKind.Never,
        DateOnly? endDate = null,
        int? max = null,
        int completedCount = 0,
        DateTime? anchorAt = null) =>
        new()
        {
            RRule = rrule,
            BaseKind = baseKind,
            EndKind = endKind,
            EndDate = endDate,
            MaxOccurrences = max,
            CompletedCount = completedCount,
            AnchorAt = anchorAt ?? Clock.UtcNow,
        };

    private static DateTime Day(int year, int month, int day) => Clock.LocalDayStartUtc(new DateOnly(year, month, day));

    [Fact]
    public void NextDue_毎日_期限日基準は元の期限の次()
    {
        var next = Engine().NextDue(DateOnlyTask(2026, 9, 22), Rule(RecurrencePresets.Daily()), FixedClock.LocalToUtc(2026, 9, 25, 9, 0));

        Assert.Equal(Day(2026, 9, 23), next);
    }

    [Fact]
    public void NextDue_完了日基準で3日遅れて完了_完了日から数える()
    {
        // 期限 9/22 の「3日ごと」を 9/25 に完了 → 9/28（元期限+3日の 9/25 ではない）
        var rule = Rule(RecurrencePresets.Daily(3), RecurrenceBaseKind.CompletedDate);

        var next = Engine().NextDue(DateOnlyTask(2026, 9, 22), rule, FixedClock.LocalToUtc(2026, 9, 25, 9, 0));

        Assert.Equal(Day(2026, 9, 28), next);
    }

    [Fact]
    public void NextDue_完了日基準_深夜の完了はローカル日付で数える()
    {
        // UTC では 9/25 だが JST では 9/26 の 0:30 に完了
        var completedAt = new DateTime(2026, 9, 25, 15, 30, 0, DateTimeKind.Utc);

        var next = Engine().NextDue(DateOnlyTask(2026, 9, 22), Rule(RecurrencePresets.Daily(), RecurrenceBaseKind.CompletedDate), completedAt);

        Assert.Equal(Day(2026, 9, 27), next);
    }

    [Fact]
    public void NextDue_毎月31日_31日のない月は飛ばす()
    {
        var next = Engine().NextDue(DateOnlyTask(2026, 1, 31), Rule(RecurrencePresets.MonthlyByDay(31)), Clock.UtcNow);

        Assert.Equal(Day(2026, 3, 31), next);
    }

    [Fact]
    public void NextDue_月末指定_短い月はその月の末日()
    {
        var next = Engine().NextDue(DateOnlyTask(2026, 1, 31), Rule(RecurrencePresets.MonthlyByDay(-1)), Clock.UtcNow);

        Assert.Equal(Day(2026, 2, 28), next);
    }

    [Fact]
    public void NextDue_月末指定_年をまたぐ()
    {
        var next = Engine().NextDue(DateOnlyTask(2026, 12, 31), Rule(RecurrencePresets.MonthlyByDay(-1)), Clock.UtcNow);

        Assert.Equal(Day(2027, 1, 31), next);
    }

    [Fact]
    public void NextDue_毎月第3月曜()
    {
        // 2026-09-21 は9月の第3月曜 → 次は 10月の第3月曜 10/19
        var next = Engine().NextDue(DateOnlyTask(2026, 9, 21), Rule(RecurrencePresets.MonthlyByWeekday(3, DayOfWeek.Monday)), Clock.UtcNow);

        Assert.Equal(Day(2026, 10, 19), next);
    }

    [Fact]
    public void NextDue_うるう年の2月29日_4年に1回()
    {
        var next = Engine().NextDue(DateOnlyTask(2024, 2, 29), Rule("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29"), Clock.UtcNow);

        Assert.Equal(Day(2028, 2, 29), next);
    }

    [Fact]
    public void NextDue_平日のみ_金曜の次は月曜()
    {
        var next = Engine().NextDue(DateOnlyTask(2026, 9, 25), Rule(RecurrencePresets.Weekdays()), Clock.UtcNow);

        Assert.Equal(Day(2026, 9, 28), next);
    }

    [Fact]
    public void NextDue_平日のみ_祝日回避がONなら翌営業日へ送る()
    {
        // 9/18（金）の次は 9/21（月・祝）→ 9/22（火）
        var engine = Engine(shiftOnHolidays: true, new DateOnly(2026, 9, 21));

        var next = engine.NextDue(DateOnlyTask(2026, 9, 18), Rule(RecurrencePresets.Weekdays()), Clock.UtcNow);

        Assert.Equal(Day(2026, 9, 22), next);
    }

    [Fact]
    public void NextDue_平日のみ_祝日回避がOFFなら祝日のまま()
    {
        var engine = Engine(shiftOnHolidays: false, new DateOnly(2026, 9, 21));

        var next = engine.NextDue(DateOnlyTask(2026, 9, 18), Rule(RecurrencePresets.Weekdays()), Clock.UtcNow);

        Assert.Equal(Day(2026, 9, 21), next);
    }

    [Fact]
    public void NextDue_平日以外の繰り返しは祝日でも送らない()
    {
        var engine = Engine(shiftOnHolidays: true, new DateOnly(2026, 9, 23));

        var next = engine.NextDue(DateOnlyTask(2026, 9, 22), Rule(RecurrencePresets.Daily()), Clock.UtcNow);

        Assert.Equal(Day(2026, 9, 23), next);
    }

    [Fact]
    public void NextDue_時刻ありは時刻を保つ()
    {
        var due = FixedClock.LocalToUtc(2026, 9, 22, 15, 0);

        var next = Engine().NextDue(TimedTask(due), Rule(RecurrencePresets.Daily()), FixedClock.LocalToUtc(2026, 9, 22, 18, 0));

        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, 15, 0), next);
    }

    [Fact]
    public void NextDue_完了日基準で時刻あり_日付は完了日_時刻は元の期限()
    {
        var due = FixedClock.LocalToUtc(2026, 9, 22, 15, 0);
        var rule = Rule(RecurrencePresets.Daily(3), RecurrenceBaseKind.CompletedDate);

        var next = Engine().NextDue(TimedTask(due), rule, FixedClock.LocalToUtc(2026, 9, 25, 18, 30));

        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 28, 15, 0), next);
    }

    [Fact]
    public void NextDue_期限なしのタスク_AnchorAtから数える()
    {
        var rule = Rule(RecurrencePresets.Daily(), anchorAt: FixedClock.LocalToUtc(2026, 9, 20, 13, 0));

        var next = Engine().NextDue(new TaskItem(), rule, Clock.UtcNow);

        Assert.Equal(Day(2026, 9, 21), next);
    }

    [Fact]
    public void NextDue_終了日を過ぎたら次回なし()
    {
        var rule = Rule(RecurrencePresets.Daily(), endKind: RecurrenceEndKind.UntilDate, endDate: new DateOnly(2026, 9, 22));

        Assert.Null(Engine().NextDue(DateOnlyTask(2026, 9, 22), rule, Clock.UtcNow));
    }

    [Fact]
    public void NextDue_終了日ちょうどは生成する()
    {
        var rule = Rule(RecurrencePresets.Daily(), endKind: RecurrenceEndKind.UntilDate, endDate: new DateOnly(2026, 9, 23));

        Assert.Equal(Day(2026, 9, 23), Engine().NextDue(DateOnlyTask(2026, 9, 22), rule, Clock.UtcNow));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void NextDue_回数指定は完了回数プラス1で打ち切る(int completedCount, bool expectNext)
    {
        var rule = Rule(RecurrencePresets.Daily(), endKind: RecurrenceEndKind.Count, max: 3, completedCount: completedCount);

        var next = Engine().NextDue(DateOnlyTask(2026, 9, 22), rule, Clock.UtcNow);

        Assert.Equal(expectNext, next is not null);
    }

    [Fact]
    public void NextDue_壊れたRRULE_次回なし()
    {
        Assert.Null(Engine().NextDue(DateOnlyTask(2026, 9, 22), Rule("こわれている"), Clock.UtcNow));
        Assert.Null(Engine().NextDue(DateOnlyTask(2026, 9, 22), Rule(""), Clock.UtcNow));
    }

    [Fact]
    public void NextDueForSkip_完了日基準のルールでも期限日から数える()
    {
        var rule = Rule(RecurrencePresets.Daily(), RecurrenceBaseKind.CompletedDate);

        Assert.Equal(Day(2026, 9, 23), Engine().NextDueForSkip(DateOnlyTask(2026, 9, 22), rule));
    }

    [Fact]
    public void NextDueForSkip_回数が尽きていたら送らない()
    {
        var rule = Rule(RecurrencePresets.Daily(), endKind: RecurrenceEndKind.Count, max: 2, completedCount: 1);

        Assert.Null(Engine().NextDueForSkip(DateOnlyTask(2026, 9, 22), rule));
    }

    [Fact]
    public void Preview_次回とその次を返す()
    {
        var preview = Engine().Preview(
            new RecurrenceInput(RecurrencePresets.Weekly([DayOfWeek.Monday])),
            Day(2026, 9, 21),
            dueHasTime: false,
            count: 2);

        Assert.Equal([Day(2026, 9, 28), Day(2026, 10, 5)], preview);
    }

    [Fact]
    public void Preview_回数指定は残りぶんまで()
    {
        var input = new RecurrenceInput(RecurrencePresets.Daily(), EndKind: RecurrenceEndKind.Count, MaxOccurrences: 2);

        var preview = Engine().Preview(input, Day(2026, 9, 22), false, 3);

        Assert.Equal([Day(2026, 9, 23)], preview);
    }

    [Fact]
    public void Preview_終了日を超えない()
    {
        var input = new RecurrenceInput(RecurrencePresets.Daily(), EndKind: RecurrenceEndKind.UntilDate, EndDate: new DateOnly(2026, 9, 24));

        Assert.Equal([Day(2026, 9, 23), Day(2026, 9, 24)], Engine().Preview(input, Day(2026, 9, 22), false, 5));
    }

    [Fact]
    public void Preview_件数0や壊れたRRULE_空()
    {
        Assert.Empty(Engine().Preview(new RecurrenceInput(RecurrencePresets.Daily()), Day(2026, 9, 22), false, 0));
        Assert.Empty(Engine().Preview(new RecurrenceInput("???"), Day(2026, 9, 22), false, 3));
    }

    [Fact]
    public void Preview_時刻ありは時刻を保つ()
    {
        var baseDue = FixedClock.LocalToUtc(2026, 9, 22, 9, 30);

        var preview = Engine().Preview(new RecurrenceInput(RecurrencePresets.Daily()), baseDue, true, 2);

        Assert.Equal([FixedClock.LocalToUtc(2026, 9, 23, 9, 30), FixedClock.LocalToUtc(2026, 9, 24, 9, 30)], preview);
    }

    [Fact]
    public void Occurrences_現在の期限より後で範囲内だけ返す()
    {
        var occurrences = Engine().Occurrences(
            Rule(RecurrencePresets.Daily()),
            Day(2026, 9, 22),
            dueHasTime: false,
            fromUtc: Day(2026, 9, 24),
            toUtc: Day(2026, 9, 27));

        Assert.Equal([Day(2026, 9, 24), Day(2026, 9, 25), Day(2026, 9, 26)], occurrences);
    }

    [Fact]
    public void Occurrences_回数指定は残りぶんだけ()
    {
        var rule = Rule(RecurrencePresets.Daily(), endKind: RecurrenceEndKind.Count, max: 3, completedCount: 0);

        var occurrences = Engine().Occurrences(rule, Day(2026, 9, 22), false, Day(2026, 9, 1), Day(2026, 10, 1));

        Assert.Equal([Day(2026, 9, 23), Day(2026, 9, 24)], occurrences);
    }

    [Fact]
    public void Occurrences_終了日で止まる()
    {
        var rule = Rule(RecurrencePresets.Daily(), endKind: RecurrenceEndKind.UntilDate, endDate: new DateOnly(2026, 9, 24));

        var occurrences = Engine().Occurrences(rule, Day(2026, 9, 22), false, Day(2026, 9, 1), Day(2026, 10, 1));

        Assert.Equal([Day(2026, 9, 23), Day(2026, 9, 24)], occurrences);
    }

    [Fact]
    public void Occurrences_祝日回避が反映され重なった回は出さない()
    {
        // 9/23（水）が祝日。平日繰り返しなので 9/23 は 9/24 へ送られ、本来の 9/24 と重なるので1回ぶんになる
        var engine = Engine(shiftOnHolidays: true, new DateOnly(2026, 9, 23));

        var occurrences = engine.Occurrences(Rule(RecurrencePresets.Weekdays()), Day(2026, 9, 22), false, Day(2026, 9, 22), Day(2026, 9, 26));

        Assert.Equal([Day(2026, 9, 24), Day(2026, 9, 25)], occurrences);
    }
}

public class RecurrenceTextTests
{
    [Theory]
    [InlineData("FREQ=DAILY", "毎日")]
    [InlineData("FREQ=DAILY;INTERVAL=3", "3日ごと")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "平日")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", "毎週 月")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR", "毎週 月・水・金")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU", "隔週 火")]
    [InlineData("FREQ=WEEKLY;INTERVAL=3;BYDAY=TU", "3週ごと 火")]
    [InlineData("FREQ=WEEKLY", "毎週")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=1", "毎月 1日")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1", "毎月 月末")]
    [InlineData("FREQ=MONTHLY;BYDAY=3MO", "毎月 第3月曜")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "毎月 最終金曜")]
    [InlineData("FREQ=MONTHLY", "毎月")]
    [InlineData("FREQ=YEARLY", "毎年")]
    public void Describe_日本語にする(string rrule, string expected) =>
        Assert.Equal(expected, RecurrenceText.Describe(rrule));

    [Theory]
    [InlineData("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29")]
    [InlineData("FREQ=DAILY;COUNT=3")]
    [InlineData("こわれている")]
    [InlineData("")]
    public void Describe_読めない形はそのまま返す(string rrule) =>
        Assert.Equal(rrule, RecurrenceText.Describe(rrule));

    [Fact]
    public void Describe_プリセットと往復する()
    {
        Assert.Equal("平日", RecurrenceText.Describe(RecurrencePresets.Weekdays()));
        Assert.Equal("毎週 月・水・金", RecurrenceText.Describe(RecurrencePresets.Weekly([DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday])));
        Assert.Equal("毎月 月末", RecurrenceText.Describe(RecurrencePresets.MonthlyByDay(-1)));
        Assert.Equal("毎月 第2木曜", RecurrenceText.Describe(RecurrencePresets.MonthlyByWeekday(2, DayOfWeek.Thursday)));
        Assert.Equal("毎月", RecurrenceText.Describe(RecurrencePresets.Monthly()));
    }
}

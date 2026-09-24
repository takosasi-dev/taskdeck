using TaskDeck.App.Controls.Pickers;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Templates;

/// <summary>繰り返しピッカーの中身。時計は 2026-09-22（火）10:00 JST、次回の計算は本物の RecurrenceEngine。</summary>
public class RecurrencePickerModelTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private static RecurrencePickerModel Model(bool weekStartsOnMonday = false) =>
        new(new RecurrenceEngine(Clock, new FixedHolidays(), new FakeSettingsStore()), Clock, weekStartsOnMonday);

    /// <summary>期限 9/22（日付のみ）のタスクとして開く。</summary>
    private static RecurrencePickerModel Opened(RecurrenceInput? input)
    {
        var model = Model();
        model.Load(input, Clock.LocalDayStartUtc(new DateOnly(2026, 9, 22)), false);
        return model;
    }

    private static void Select(RecurrencePickerModel model, params DayOfWeek[] days)
    {
        foreach (var toggle in model.Weekdays)
        {
            toggle.IsSelected = days.Contains(toggle.Day);
        }
    }

    [Theory]
    [InlineData(RecurrenceChoice.Daily, "FREQ=DAILY")]
    [InlineData(RecurrenceChoice.Weekdays, "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    [InlineData(RecurrenceChoice.Weekly, "FREQ=WEEKLY;BYDAY=TU")]
    [InlineData(RecurrenceChoice.Monthly, "FREQ=MONTHLY;BYMONTHDAY=22")]
    [InlineData(RecurrenceChoice.Yearly, "FREQ=YEARLY")]
    [InlineData(RecurrenceChoice.Custom, "FREQ=DAILY;INTERVAL=2")]
    public void Build_プリセットを選ぶ_期限の日付から既定のRRULEになる(RecurrenceChoice choice, string expected)
    {
        var model = Opened(null);

        model.Choice = choice;

        Assert.Equal(new RecurrenceInput(expected), model.Build());
    }

    [Fact]
    public void Build_なし_nullを返す()
    {
        var model = Opened(new RecurrenceInput(RecurrencePresets.Daily()));

        model.Choice = RecurrenceChoice.None;

        Assert.Null(model.Build());
        Assert.Equal("繰り返しません", model.PreviewMessage);
    }

    [Fact]
    public void Build_毎週で曜日を複数選ぶ_月曜始まりの順で並ぶ()
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Weekly;

        Select(model, DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday);

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,WE,FR", model.Build()!.RRule);
    }

    [Fact]
    public void Build_毎週で曜日を選ばない_期限の曜日で繰り返すと案内する()
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Weekly;

        Select(model);

        Assert.Equal("FREQ=WEEKLY", model.Build()!.RRule);
        Assert.Equal("曜日を選ばないと、期限の曜日（火）で繰り返します", model.WeekdayHint);
    }

    [Fact]
    public void Build_毎月の第N曜日_期限の日から第4火曜になる()
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Monthly;

        model.MonthlyMode = MonthlyMode.NthWeekday;

        Assert.Equal("FREQ=MONTHLY;BYDAY=4TU", model.Build()!.RRule);
    }

    [Fact]
    public void Build_毎月の月末_BYMONTHDAYがマイナス1()
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Monthly;

        model.MonthDay = -1;

        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=-1", model.Build()!.RRule);
        Assert.Null(model.MonthDayHint);
    }

    [Fact]
    public void MonthDay_29日以降_ない月を飛ばすと案内する()
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Monthly;

        model.MonthDay = 31;

        Assert.Equal("31日がない月は飛ばします。毎月の最後の日にするなら「月末」を選びます", model.MonthDayHint);
    }

    [Fact]
    public void MonthNth_曜日側の欄を変える_曜日指定に切り替わる()
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Monthly;

        model.MonthNth = -1;
        model.MonthWeekday = DayOfWeek.Friday;

        Assert.Equal(MonthlyMode.NthWeekday, model.MonthlyMode);
        Assert.Equal("FREQ=MONTHLY;BYDAY=-1FR", model.Build()!.RRule);
    }

    [Theory]
    [InlineData(IntervalUnit.Day, "3", "FREQ=DAILY;INTERVAL=3")]
    [InlineData(IntervalUnit.Week, "2", "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,TH")]
    [InlineData(IntervalUnit.Month, "3", "FREQ=MONTHLY;INTERVAL=3")]
    [InlineData(IntervalUnit.Day, "１０", "FREQ=DAILY;INTERVAL=10")]
    public void Build_カスタム_N日N週Nか月ごとになる(IntervalUnit unit, string interval, string expected)
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Custom;
        Select(model, DayOfWeek.Monday, DayOfWeek.Thursday);

        model.Unit = unit;
        model.IntervalText = interval;

        Assert.Equal(expected, model.Build()!.RRule);
    }

    [Fact]
    public void Build_起点と終了条件_入力のとおりに入る()
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Daily;
        model.BaseKind = RecurrenceBaseKind.CompletedDate;

        model.EndKind = RecurrenceEndKind.Count;
        model.EndCountText = "5";
        var byCount = model.Build();
        model.EndKind = RecurrenceEndKind.UntilDate;
        model.EndDate = new DateTime(2026, 12, 31);
        var byDate = model.Build();

        Assert.Equal(new RecurrenceInput("FREQ=DAILY", RecurrenceBaseKind.CompletedDate, RecurrenceEndKind.Count, MaxOccurrences: 5), byCount);
        Assert.Equal(new RecurrenceInput("FREQ=DAILY", RecurrenceBaseKind.CompletedDate, RecurrenceEndKind.UntilDate, EndDate: new DateOnly(2026, 12, 31)), byDate);
    }

    [Fact]
    public void EndKind_日付までにする_起点の1か月後を既定に入れる()
    {
        var model = Opened(new RecurrenceInput(RecurrencePresets.Daily()));

        model.EndKind = RecurrenceEndKind.UntilDate;

        Assert.Equal(new DateTime(2026, 10, 22), model.EndDate);
        Assert.True(model.CanCommit);
    }

    [Theory]
    [InlineData("FREQ=DAILY", RecurrenceChoice.Daily)]
    [InlineData("FREQ=DAILY;INTERVAL=3", RecurrenceChoice.Custom)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", RecurrenceChoice.Weekdays)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR", RecurrenceChoice.Weekly)]
    [InlineData("FREQ=WEEKLY", RecurrenceChoice.Weekly)]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU", RecurrenceChoice.Custom)]
    [InlineData("FREQ=WEEKLY;INTERVAL=2", RecurrenceChoice.Custom)]
    [InlineData("FREQ=MONTHLY", RecurrenceChoice.Custom)]
    [InlineData("FREQ=MONTHLY;INTERVAL=3", RecurrenceChoice.Custom)]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=31", RecurrenceChoice.Monthly)]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1", RecurrenceChoice.Monthly)]
    [InlineData("FREQ=MONTHLY;BYDAY=3MO", RecurrenceChoice.Monthly)]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", RecurrenceChoice.Monthly)]
    [InlineData("FREQ=YEARLY", RecurrenceChoice.Yearly)]
    public void Load_アプリが作るRRULE_画面に写して決定すると元に戻る(string rrule, RecurrenceChoice expectedChoice)
    {
        var input = new RecurrenceInput(rrule, RecurrenceBaseKind.CompletedDate, RecurrenceEndKind.Count, MaxOccurrences: 4);

        var model = Opened(input);

        Assert.Equal(expectedChoice, model.Choice);
        Assert.False(model.IsRaw);
        Assert.True(model.HadRecurrence);
        Assert.Equal(input, model.Build());
    }

    [Fact]
    public void Load_曜日指定の毎週_選んだ曜日だけがオンになる()
    {
        var model = Opened(new RecurrenceInput("FREQ=WEEKLY;BYDAY=MO,WE"));

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], model.Weekdays.Where(w => w.IsSelected).Select(w => w.Day).Order());
    }

    [Fact]
    public void Load_毎月第3月曜_曜日指定の欄に写る()
    {
        var model = Opened(new RecurrenceInput("FREQ=MONTHLY;BYDAY=3MO"));

        Assert.Equal(MonthlyMode.NthWeekday, model.MonthlyMode);
        Assert.Equal(3, model.MonthNth);
        Assert.Equal(DayOfWeek.Monday, model.MonthWeekday);
    }

    [Fact]
    public void Load_日付までの終了条件_終了日と起点がそのまま写る()
    {
        var input = new RecurrenceInput("FREQ=DAILY", RecurrenceBaseKind.CompletedDate, RecurrenceEndKind.UntilDate, EndDate: new DateOnly(2026, 11, 30));

        var model = Opened(input);

        Assert.Equal(RecurrenceBaseKind.CompletedDate, model.BaseKind);
        Assert.Equal(new DateTime(2026, 11, 30), model.EndDate);
        Assert.True(model.IsEndDate);
        Assert.Equal(input, model.Build());
    }

    [Theory]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=29;BYMONTH=2")]
    [InlineData("FREQ=WEEKLY;BYDAY=FR,MO")]
    [InlineData("FREQ=DAILY;COUNT=5")]
    [InlineData("FREQ=DAILY;INTERVAL=1")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=40")]
    [InlineData("FREQ=HOURLY")]
    [InlineData("GARBAGE")]
    public void Load_画面で表せないRRULE_カスタムとして見せてそのまま返す(string rrule)
    {
        var model = Opened(new RecurrenceInput(rrule));

        Assert.Equal(RecurrenceChoice.Custom, model.Choice);
        Assert.True(model.IsRaw);
        Assert.StartsWith("いまの設定: ", model.RawRuleText);
        Assert.Equal(rrule, model.Build()!.RRule);
    }

    [Fact]
    public void Load_読めないRRULEのあとで間隔を変える_画面の値で置き換わる()
    {
        var model = Opened(new RecurrenceInput("FREQ=MONTHLY;BYMONTHDAY=29;BYMONTH=2"));

        model.IntervalText = "4";

        Assert.False(model.IsRaw);
        Assert.Equal("FREQ=DAILY;INTERVAL=4", model.Build()!.RRule);
    }

    [Fact]
    public void Load_読めないRRULEで起点だけ変える_RRULEは保つ()
    {
        var model = Opened(new RecurrenceInput("FREQ=DAILY;COUNT=5"));

        model.BaseKind = RecurrenceBaseKind.CompletedDate;

        Assert.Equal(new RecurrenceInput("FREQ=DAILY;COUNT=5", RecurrenceBaseKind.CompletedDate), model.Build());
    }

    [Fact]
    public void Load_開き直す_前回の編集は残らない()
    {
        var model = Opened(new RecurrenceInput(RecurrencePresets.Weekly([DayOfWeek.Monday])));
        model.Choice = RecurrenceChoice.Yearly;

        model.Load(new RecurrenceInput(RecurrencePresets.Weekly([DayOfWeek.Monday])), Clock.LocalDayStartUtc(new DateOnly(2026, 9, 22)), false);

        Assert.Equal(RecurrenceChoice.Weekly, model.Choice);
    }

    [Fact]
    public void Preview_毎週月曜_次回とその次を日付で出す()
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Weekly;

        Select(model, DayOfWeek.Monday);

        Assert.Equal("9月28日（月）", model.NextText);
        Assert.Equal("10月5日（月）", model.AfterNextText);
        Assert.True(model.HasPreviewDates);
        Assert.Null(model.PreviewMessage);
    }

    [Fact]
    public void Preview_時刻ありの期限_時刻も出す()
    {
        var model = Model();
        model.Load(new RecurrenceInput(RecurrencePresets.Daily()), FixedClock.LocalToUtc(2026, 9, 22, 15, 0), true);

        Assert.Equal("9月23日（水） 15:00", model.NextText);
        Assert.Equal("9月24日（木） 15:00", model.AfterNextText);
    }

    [Fact]
    public void Preview_期限なし_今日から数える()
    {
        var model = Model();
        model.Load(new RecurrenceInput(RecurrencePresets.Daily()), null, true);

        Assert.Equal(new DateOnly(2026, 9, 22), model.BaseDate);
        Assert.Equal("9月23日（水）", model.NextText);
    }

    [Fact]
    public void Preview_来年になる日付_年を添える()
    {
        var model = Opened(new RecurrenceInput(RecurrencePresets.Yearly()));

        Assert.Equal("2027年9月22日（水）", model.NextText);
        Assert.Equal("9月22日", model.YearlyHint);
    }

    [Fact]
    public void Preview_2回で終わる_その次はここで終わると出す()
    {
        var model = Opened(new RecurrenceInput(RecurrencePresets.Daily(), EndKind: RecurrenceEndKind.Count, MaxOccurrences: 2));

        Assert.Equal("9月23日（水）", model.NextText);
        Assert.Equal("なし（ここで終わります）", model.AfterNextText);
    }

    [Fact]
    public void Preview_1回で終わる_この先の予定はないと出す()
    {
        var model = Opened(new RecurrenceInput(RecurrencePresets.Daily(), EndKind: RecurrenceEndKind.Count, MaxOccurrences: 1));

        Assert.Null(model.NextText);
        Assert.False(model.HasPreviewDates);
        Assert.Equal("この先の予定はありません", model.PreviewMessage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1000")]
    [InlineData("")]
    [InlineData("三")]
    public void IntervalText_範囲外や数でない_決定できず理由を出す(string text)
    {
        var model = Opened(null);
        model.Choice = RecurrenceChoice.Custom;

        model.IntervalText = text;

        Assert.False(model.CanCommit);
        Assert.Equal("間隔は 1〜999 の数で入れてください", model.Error);
        Assert.Equal(model.Error, model.PreviewMessage);
        Assert.Throws<InvalidOperationException>(() => model.Build());
    }

    [Fact]
    public void EndCountText_数でない_決定できない()
    {
        var model = Opened(new RecurrenceInput(RecurrencePresets.Daily()));
        model.EndKind = RecurrenceEndKind.Count;

        model.EndCountText = "x";

        Assert.False(model.CanCommit);
        Assert.Equal("回数は 1〜999 の数で入れてください", model.Error);
    }

    [Theory]
    [InlineData(RecurrenceChoice.None, 1, RecurrenceChoice.Daily)]
    [InlineData(RecurrenceChoice.Weekly, 1, RecurrenceChoice.Monthly)]
    [InlineData(RecurrenceChoice.None, -1, RecurrenceChoice.Custom)]
    [InlineData(RecurrenceChoice.Custom, 1, RecurrenceChoice.None)]
    public void MoveChoice_矢印キー_隣のプリセットへ移り端では回る(RecurrenceChoice from, int delta, RecurrenceChoice expected)
    {
        var model = Opened(null);
        model.Choice = from;

        model.MoveChoice(delta);

        Assert.Equal(expected, model.Choice);
    }

    [Fact]
    public void Toggle_2択の矢印キー_もう一方へ切り替わる()
    {
        var model = Opened(null);

        model.ToggleBaseKind();
        model.ToggleMonthlyMode();

        Assert.Equal(RecurrenceBaseKind.CompletedDate, model.BaseKind);
        Assert.Equal(MonthlyMode.NthWeekday, model.MonthlyMode);
    }

    [Fact]
    public void Weekdays_月曜始まりの設定_月曜から並ぶ()
    {
        var model = Model(weekStartsOnMonday: true);

        Assert.Equal(DayOfWeek.Monday, model.Weekdays[0].Day);
        Assert.Equal(DayOfWeek.Sunday, model.Weekdays[6].Day);
        Assert.Equal("月", model.Weekdays[0].Label);
    }

    [Fact]
    public void HadRecurrence_なしで開く_やめるボタンを出さない()
    {
        Assert.False(Opened(null).HadRecurrence);
        Assert.False(Opened(null).HasRule);
    }
}

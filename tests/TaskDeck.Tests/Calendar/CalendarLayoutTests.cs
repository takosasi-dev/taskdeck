using NSubstitute;
using TaskDeck.App.Views.Calendar;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Calendar;

/// <summary>表示範囲・日への振り分け・週表示の並べ方・ドラッグの値（CalendarRange / CalendarBuilder / CalendarDrag）。時計は JST 2026-09-22（火）10:00。</summary>
public class CalendarLayoutTests
{
    private readonly CalendarFixture _f = new();

    private static DateOnly Day(int month, int day) => CalendarFixture.Day(month, day);

    // ---- 表示範囲 ----

    [Fact]
    public void MonthGridStart_日曜始まり_1日を含む週の日曜から()
    {
        // 2026年9月1日は火曜
        Assert.Equal(Day(8, 30), CalendarRange.MonthGridStart(Day(9, 15), DayOfWeek.Sunday));
    }

    [Fact]
    public void MonthGridStart_月曜始まり_1日を含む週の月曜から()
    {
        Assert.Equal(Day(8, 31), CalendarRange.MonthGridStart(Day(9, 15), DayOfWeek.Monday));
    }

    [Fact]
    public void MonthGridStart_1日が週の初日_前の月の週を入れない()
    {
        // 2026年11月1日は日曜
        Assert.Equal(Day(11, 1), CalendarRange.MonthGridStart(Day(11, 20), DayOfWeek.Sunday));
        Assert.Equal(Day(10, 26), CalendarRange.MonthGridStart(Day(11, 20), DayOfWeek.Monday));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, 20)]
    [InlineData(DayOfWeek.Monday, 21)]
    public void WeekStart_週の開始曜日_その曜日まで戻る(DayOfWeek firstDay, int expectedDay)
    {
        Assert.Equal(Day(9, expectedDay), CalendarRange.WeekStart(Day(9, 22), firstDay));
    }

    [Fact]
    public void WeekStart_初日そのもの_動かない()
    {
        Assert.Equal(Day(9, 20), CalendarRange.WeekStart(Day(9, 20), DayOfWeek.Sunday));
    }

    [Fact]
    public void ToUtc_月表示の42日_ローカルの0時をUTCに直した境界()
    {
        var (from, to) = CalendarRange.ToUtc(Day(8, 30), CalendarRange.MonthDays, _f.Clock);

        Assert.Equal(FixedClock.LocalToUtc(2026, 8, 30), from);
        Assert.Equal(FixedClock.LocalToUtc(2026, 10, 11), to);
        Assert.Equal(new DateTime(2026, 8, 29, 15, 0, 0, DateTimeKind.Utc), from);
    }

    [Fact]
    public void MonthTitle_年と月()
    {
        Assert.Equal("2026年9月", CalendarRange.MonthTitle(Day(9, 22)));
    }

    [Theory]
    [InlineData(9, 21, "9月21日〜27日")]
    [InlineData(9, 28, "9月28日〜10月4日")]
    public void WeekTitle_月をまたぐときだけ終わりにも月を書く(int month, int day, string expected)
    {
        Assert.Equal(expected, CalendarRange.WeekTitle(Day(month, day)));
    }

    [Fact]
    public void WeekTitle_年をまたぐ_両方に年を書く()
    {
        Assert.Equal("2026年12月27日〜2027年1月2日", CalendarRange.WeekTitle(Day(12, 27)));
    }

    // ---- 日への振り分け ----

    [Fact]
    public void Build_深夜の期限_ローカルの日付のセルに入る()
    {
        // 9/23 0:30 JST は UTC では 9/22 15:30
        var afterMidnight = _f.AddTask("深夜", new DateTime(2026, 9, 23, 0, 30, 0), hasTime: true);
        var beforeMidnight = _f.AddTask("夜", new DateTime(2026, 9, 22, 23, 59, 0), hasTime: true);

        var days = Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays);

        Assert.Equal(afterMidnight.Id, Assert.Single(Cell(days, Day(9, 23)).Entries).TaskId);
        Assert.Equal(beforeMidnight.Id, Assert.Single(Cell(days, Day(9, 22)).Entries).TaskId);
        Assert.Equal(new TimeOnly(0, 30), Cell(days, Day(9, 23)).Entries[0].Time);
    }

    [Fact]
    public void Build_日付のみの期限_その日の終日として入る()
    {
        _f.AddTask("終日", new DateTime(2026, 9, 25));

        var entry = Assert.Single(Cell(Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays), Day(9, 25)).Entries);

        Assert.False(entry.HasTime);
        Assert.Equal("", entry.TimeText);
    }

    [Fact]
    public void Build_5件の日_3件だけ見せて他2件()
    {
        for (var i = 0; i < 5; i++)
        {
            _f.AddTask($"タスク{i}", new DateTime(2026, 9, 24));
        }

        var cell = Cell(Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays), Day(9, 24));

        Assert.Equal(5, cell.Entries.Count);
        Assert.Equal(3, cell.VisibleEntries.Count);
        Assert.Equal(2, cell.MoreCount);
        Assert.Equal("他 2 件", cell.MoreText);
        Assert.Equal("9月24日の他 2 件", cell.MoreAutomationName);
    }

    [Fact]
    public void Build_3件ちょうど_他N件を出さない()
    {
        for (var i = 0; i < 3; i++)
        {
            _f.AddTask($"タスク{i}", new DateTime(2026, 9, 24));
        }

        var cell = Cell(Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays), Day(9, 24));

        Assert.Equal(3, cell.VisibleEntries.Count);
        Assert.False(cell.HasMore);
    }

    [Fact]
    public void Build_1日の並び_未完了の終日と時刻順から完了は最後()
    {
        var done = _f.AddTask("完了", new DateTime(2026, 9, 24, 8, 0, 0), hasTime: true, status: TaskItemStatus.Completed);
        var late = _f.AddTask("夕方", new DateTime(2026, 9, 24, 18, 0, 0), hasTime: true);
        var early = _f.AddTask("朝", new DateTime(2026, 9, 24, 9, 0, 0), hasTime: true);
        var allDay = _f.AddTask("終日", new DateTime(2026, 9, 24));

        var cell = Cell(Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays), Day(9, 24));

        Assert.Equal([allDay.Id, early.Id, late.Id, done.Id], cell.Entries.Select(e => e.TaskId));
        Assert.True(cell.Entries[3].IsClosed);
    }

    [Fact]
    public void Build_状態_期限切れと完了を見分ける()
    {
        var overdue = _f.AddTask("遅れ", new DateTime(2026, 9, 21));
        var done = _f.AddTask("済み", new DateTime(2026, 9, 21), status: TaskItemStatus.Completed);

        var cell = Cell(Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays), Day(9, 21));

        var overdueEntry = cell.Entries.Single(e => e.TaskId == overdue.Id);
        var doneEntry = cell.Entries.Single(e => e.TaskId == done.Id);
        Assert.True(overdueEntry.IsOverdue);
        Assert.True(overdueEntry.CanDrag);
        Assert.False(doneEntry.IsOverdue);
        Assert.True(doneEntry.IsClosed);
        Assert.False(doneEntry.CanDrag);
    }

    [Fact]
    public void Build_月表示_今日と月の外の日と休日の色()
    {
        var days = Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays, month: Day(9, 1), holidays: new FixedHolidays(Day(9, 23)));

        Assert.True(Cell(days, Day(9, 22)).IsToday);
        Assert.True(Cell(days, Day(8, 31)).IsOtherMonth);
        Assert.False(Cell(days, Day(9, 30)).IsOtherMonth);
        Assert.Equal("10/1", Cell(days, Day(10, 1)).DayText);
        Assert.Equal("23", Cell(days, Day(9, 23)).DayText);
        // 祝日（9/23 水）と日曜は休日の色、土曜はアクセント
        Assert.Equal("祝日", Cell(days, Day(9, 23)).HolidayName);
        Assert.True(Cell(days, Day(9, 23)).IsHolidayColor);
        Assert.True(Cell(days, Day(9, 20)).IsHolidayColor);
        Assert.True(Cell(days, Day(9, 26)).IsSaturdayColor);
        Assert.False(Cell(days, Day(9, 24)).IsHolidayColor);
    }

    [Fact]
    public void Build_天気がある日_絵と最高最低を出し無い日は出さない()
    {
        _f.Weather.Days[Day(9, 24)] = new WeatherDay { Date = Day(9, 24), WeatherCode = 61, TemperatureMax = 24.6, TemperatureMin = 18.2, PrecipitationProbability = 70 };

        var days = Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays);

        var weather = Cell(days, Day(9, 24)).Weather;
        Assert.NotNull(weather);
        Assert.Equal(WeatherKind.Rain, weather.Kind);
        Assert.Equal("25°/18°", weather.TemperatureText);
        Assert.Equal("雨 最高 25℃ 最低 18℃ 降水 70%", weather.Description);
        Assert.Null(Cell(days, Day(9, 25)).Weather);
    }

    [Theory]
    [InlineData(0, WeatherKind.Clear)]
    [InlineData(1, WeatherKind.Clear)]
    [InlineData(2, WeatherKind.PartlyCloudy)]
    [InlineData(3, WeatherKind.Cloudy)]
    [InlineData(45, WeatherKind.Fog)]
    [InlineData(53, WeatherKind.Rain)]
    [InlineData(81, WeatherKind.Rain)]
    [InlineData(73, WeatherKind.Snow)]
    [InlineData(86, WeatherKind.Snow)]
    [InlineData(95, WeatherKind.Thunder)]
    [InlineData(5, WeatherKind.Cloudy)]
    public void WeatherLook_KindOf_WMOコードを絵に分ける(int code, WeatherKind expected)
    {
        Assert.Equal(expected, WeatherLook.KindOf(code));
    }

    [Fact]
    public void WeatherLook_From_氷点下の端数_マイナスゼロにしない()
    {
        var look = WeatherLook.From(new WeatherDay { WeatherCode = 71, TemperatureMax = 1.2, TemperatureMin = -0.4 });

        Assert.Equal("1°/0°", look!.TemperatureText);
    }

    [Fact]
    public void Build_プロジェクト色_テーマに合わせた色と薄い地()
    {
        var project = new Project { Name = "業務", ColorHex = "#CA5010" };
        _f.AddTask("資料", new DateTime(2026, 9, 24), projectId: project.Id);
        _f.AddTask("なし", new DateTime(2026, 9, 24));
        var colors = new Dictionary<Guid, string> { [project.Id] = project.ColorHex };

        var light = Cell(Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays, colors: colors), Day(9, 24)).Entries;
        var dark = Cell(Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays, colors: colors, isDark: true), Day(9, 24)).Entries;

        Assert.Equal("#CA5010", light[0].ColorHex);
        Assert.Equal("#14CA5010", light[0].TintHex);
        Assert.Equal("#FF8B56", dark[0].ColorHex);
        Assert.Equal("#2EFF8B56", dark[0].TintHex);
        Assert.Null(light[1].ColorHex);
        Assert.False(light[1].HasProjectColor);
    }

    [Fact]
    public void Build_週表示_時刻ありは時間軸へ日付のみは終日帯へ()
    {
        var timed = _f.AddTask("会議", new DateTime(2026, 9, 23, 15, 0, 0), hasTime: true, durationMinutes: 90);
        var allDay = _f.AddTask("提出", new DateTime(2026, 9, 23));

        var cell = Cell(Build(CalendarMode.Week, Day(9, 20), CalendarRange.WeekDays), Day(9, 23));

        Assert.Equal(allDay.Id, Assert.Single(cell.Entries).TaskId);
        var block = Assert.Single(cell.Blocks);
        Assert.Equal(timed.Id, block.Entry.TaskId);
        Assert.Equal(15 * 60, block.StartMinute);
        Assert.Equal((16 * 60) + 30, block.EndMinute);
        Assert.Equal("90分", block.Entry.DurationText);
    }

    [Fact]
    public void Build_範囲の外の期限_入れない()
    {
        _f.AddTask("前の週", new DateTime(2026, 9, 19));

        var days = Build(CalendarMode.Week, Day(9, 20), CalendarRange.WeekDays);

        Assert.All(days, d => Assert.Empty(d.Entries));
    }

    // ---- 繰り返しの仮表示 ----

    [Fact]
    public void Build_毎日の繰り返し_今の回より後だけを破線で出す()
    {
        var task = _f.AddTask("日報", new DateTime(2026, 9, 25));
        _f.AddRecurring(task, RecurrencePresets.Daily());

        var days = Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays);

        var real = Assert.Single(Cell(days, Day(9, 25)).Entries);
        Assert.False(real.IsGhost);
        Assert.Empty(Cell(days, Day(9, 24)).Entries);
        var ghost = Assert.Single(Cell(days, Day(9, 26)).Entries);
        Assert.True(ghost.IsGhost);
        Assert.Equal(task.Id, ghost.TaskId);
        Assert.False(ghost.CanDrag);
        Assert.Equal("繰り返し予定、日報、9月26日", ghost.AutomationName);
        // 9/26 から表示範囲の最後（10/10）まで
        Assert.Equal(15, days.SelectMany(d => d.Entries).Count(e => e.IsGhost));
    }

    [Fact]
    public void Build_毎週の繰り返し_時刻ありは週表示の時間軸に出す()
    {
        var task = _f.AddTask("定例", new DateTime(2026, 9, 14, 10, 0, 0), hasTime: true);
        _f.AddRecurring(task, RecurrencePresets.Weekly([DayOfWeek.Monday]));

        var days = Build(CalendarMode.Week, Day(9, 20), CalendarRange.WeekDays);

        var block = Assert.Single(Cell(days, Day(9, 21)).Blocks);
        Assert.True(block.Entry.IsGhost);
        Assert.Equal(10 * 60, block.StartMinute);
        Assert.Equal(1, days.Sum(d => d.Blocks.Count));
    }

    [Fact]
    public void Build_期限なしの繰り返し_出さない()
    {
        var task = _f.NewTask("いつか", null);
        _f.AddRecurring(task, RecurrencePresets.Daily());

        var days = Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays);

        Assert.All(days, d => Assert.Empty(d.Entries));
    }

    [Fact]
    public void Build_仮表示の並び_未完了の後で完了より前()
    {
        var recurring = _f.AddTask("毎日", new DateTime(2026, 9, 22));
        _f.AddRecurring(recurring, RecurrencePresets.Daily());
        var done = _f.AddTask("済み", new DateTime(2026, 9, 23), status: TaskItemStatus.Completed);
        var open = _f.AddTask("未完了", new DateTime(2026, 9, 23, 18, 0, 0), hasTime: true);

        var cell = Cell(Build(CalendarMode.Month, Day(8, 30), CalendarRange.MonthDays), Day(9, 23));

        Assert.Equal([open.Id, recurring.Id, done.Id], cell.Entries.Select(e => e.TaskId));
        Assert.True(cell.Entries[1].IsGhost);
    }

    // ---- 週表示の重なり ----

    [Fact]
    public void LayoutDay_3件が重なる_3列に並べる()
    {
        var blocks = CalendarBuilder.LayoutDay(
        [
            Timed(9, 0, 60),
            Timed(9, 30, 60),
            Timed(9, 45, 30),
        ]);

        Assert.Equal([0, 1, 2], blocks.Select(b => b.Column));
        Assert.All(blocks, b => Assert.Equal(3, b.ColumnCount));
    }

    [Fact]
    public void LayoutDay_鎖のように重なる_空いた列を使い回す()
    {
        // A 9:00-10:00 と B 9:30-10:30 が重なり、C 10:00-11:00 は B とだけ重なる → C は A の列へ
        var blocks = CalendarBuilder.LayoutDay(
        [
            Timed(9, 0, 60),
            Timed(9, 30, 60),
            Timed(10, 0, 60),
        ]);

        Assert.Equal([0, 1, 0], blocks.Select(b => b.Column));
        Assert.All(blocks, b => Assert.Equal(2, b.ColumnCount));
    }

    [Fact]
    public void LayoutDay_接しているだけ_重ならず1列ずつ()
    {
        var blocks = CalendarBuilder.LayoutDay([Timed(9, 0, 60), Timed(10, 0, 60)]);

        Assert.All(blocks, b => Assert.Equal((0, 1), (b.Column, b.ColumnCount)));
    }

    [Fact]
    public void LayoutDay_所要時間なし_30分の長さ()
    {
        var block = Assert.Single(CalendarBuilder.LayoutDay([Timed(13, 0, null)]));

        Assert.Equal((13 * 60, (13 * 60) + 30), (block.StartMinute, block.EndMinute));
    }

    [Fact]
    public void LayoutDay_深夜にかかる_24時で切る()
    {
        var block = Assert.Single(CalendarBuilder.LayoutDay([Timed(23, 30, 120)]));

        Assert.Equal(24 * 60, block.EndMinute);
    }

    // ---- ドラッグの値 ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    [InlineData(8, 15)]
    [InlineData(607, 600)]
    [InlineData(608, 615)]
    [InlineData(-30, 0)]
    [InlineData(1439, 1425)]
    public void Snap_15分単位に丸めて一日に収める(double minutes, int expected)
    {
        Assert.Equal(expected, CalendarDrag.Snap(minutes));
    }

    [Fact]
    public void ToDate_時刻あり_時刻を保って日付だけ変える()
    {
        var task = _f.NewTask("会議", new DateTime(2026, 9, 22, 15, 30, 0), hasTime: true, durationMinutes: 45);

        var change = CalendarDrag.ToDate(task, Day(9, 25), _f.Clock);

        Assert.Equal(new DueChange(FixedClock.LocalToUtc(2026, 9, 25, 15, 30), true, 45), change);
    }

    [Fact]
    public void ToDate_日付のみ_日付のみのまま()
    {
        var task = _f.NewTask("提出", new DateTime(2026, 9, 22));

        var change = CalendarDrag.ToDate(task, Day(9, 25), _f.Clock);

        Assert.Equal(new DueChange(FixedClock.LocalToUtc(2026, 9, 25), false, null), change);
    }

    [Fact]
    public void ToTime_日付のみから時間軸へ_時刻ありになり15分単位()
    {
        var task = _f.NewTask("提出", new DateTime(2026, 9, 22));

        var change = CalendarDrag.ToTime(task, Day(9, 24), (10 * 60) + 7, _f.Clock);

        Assert.Equal(new DueChange(FixedClock.LocalToUtc(2026, 9, 24, 10, 0), true, null), change);
    }

    [Fact]
    public void ToAllDay_時刻ありから終日帯へ_日付のみになる()
    {
        var task = _f.NewTask("会議", new DateTime(2026, 9, 22, 15, 0, 0), hasTime: true, durationMinutes: 60);

        var change = CalendarDrag.ToAllDay(task, Day(9, 23), _f.Clock);

        Assert.Equal(new DueChange(FixedClock.LocalToUtc(2026, 9, 23), false, 60), change);
    }

    [Fact]
    public void ResizeBottom_下端_始まりを保ち所要時間を15分単位に()
    {
        var task = _f.NewTask("作業", new DateTime(2026, 9, 22, 13, 0, 0), hasTime: true);

        var change = CalendarDrag.ResizeBottom(task, (14 * 60) + 32, _f.Clock);

        Assert.Equal(new DueChange(task.DueAt!.Value, true, 90), change);
    }

    [Fact]
    public void ResizeBottom_始まりより上_15分は残す()
    {
        var task = _f.NewTask("作業", new DateTime(2026, 9, 22, 13, 0, 0), hasTime: true, durationMinutes: 60);

        var change = CalendarDrag.ResizeBottom(task, 12 * 60, _f.Clock);

        Assert.Equal(15, change.DurationMinutes);
    }

    [Fact]
    public void ResizeTop_上端_終わりを保って始まりと所要時間が変わる()
    {
        var task = _f.NewTask("作業", new DateTime(2026, 9, 22, 13, 0, 0), hasTime: true, durationMinutes: 60);

        var change = CalendarDrag.ResizeTop(task, (12 * 60) + 20, _f.Clock);

        Assert.Equal(new DueChange(FixedClock.LocalToUtc(2026, 9, 22, 12, 15), true, 105), change);
    }

    [Fact]
    public void ResizeTop_終わりより下_15分は残す()
    {
        var task = _f.NewTask("作業", new DateTime(2026, 9, 22, 13, 0, 0), hasTime: true, durationMinutes: 60);

        var change = CalendarDrag.ResizeTop(task, 16 * 60, _f.Clock);

        Assert.Equal(new DueChange(FixedClock.LocalToUtc(2026, 9, 22, 13, 45), true, 15), change);
    }

    // ---- 補助 ----

    private IReadOnlyList<CalendarDay> Build(
        CalendarMode mode,
        DateOnly first,
        int count,
        DateOnly? month = null,
        IHolidayProvider? holidays = null,
        IReadOnlyDictionary<Guid, string>? colors = null,
        bool isDark = false)
    {
        var fixedHolidays = holidays ?? new FixedHolidays();
        var source = new CalendarSource(_f.Items, _f.Recurring, colors ?? new Dictionary<Guid, string>(), isDark);
        return CalendarBuilder.Build(
            mode,
            first,
            count,
            source,
            new RecurrenceEngine(_f.Clock, fixedHolidays, _f.Settings),
            fixedHolidays,
            _f.Weather,
            _f.Clock,
            month ?? (mode == CalendarMode.Month ? Day(9, 1) : (DateOnly?)null));
    }

    private static CalendarDay Cell(IReadOnlyList<CalendarDay> days, DateOnly date) => days.Single(d => d.Date == date);

    private CalendarEntry Timed(int hour, int minute, int? duration) => new()
    {
        Task = _f.NewTask("t", new DateTime(2026, 9, 22, hour, minute, 0), hasTime: true, durationMinutes: duration),
        Date = Day(9, 22),
        Time = new TimeOnly(hour, minute),
        IsGhost = false,
        IsOverdue = false,
        ColorHex = null,
        TintHex = null,
        AutomationName = "t",
    };
}

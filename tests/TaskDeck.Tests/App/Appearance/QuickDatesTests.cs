using TaskDeck.App.Controls.Pickers;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>日付ピッカーのクイック選択と月カレンダーの日付計算（UI 設計書 12.1、F-194、F-125）。</summary>
public class QuickDatesTests
{
    // 2026-09-23 は水曜。2026 年のシルバーウィーク（9/21 敬老の日・9/22 国民の休日・9/23 秋分の日）
    private static readonly DateOnly Wednesday = FixedClock.AtLocal(2026, 9, 23, 10, 0).LocalToday();

    [Fact]
    public void ThisWeekend_Wednesday_ReturnsComingSaturday() =>
        Assert.Equal(new DateOnly(2026, 9, 26), QuickDates.ThisWeekend(Wednesday));

    [Fact]
    public void ThisWeekend_Saturday_ReturnsSameDay() =>
        Assert.Equal(new DateOnly(2026, 9, 26), QuickDates.ThisWeekend(new DateOnly(2026, 9, 26)));

    [Fact]
    public void ThisWeekend_Sunday_ReturnsSaturdayOfTheNextWeek() =>
        Assert.Equal(new DateOnly(2026, 10, 3), QuickDates.ThisWeekend(new DateOnly(2026, 9, 27)));

    [Fact]
    public void NextWeek_Wednesday_ReturnsNextMonday() =>
        Assert.Equal(new DateOnly(2026, 9, 28), QuickDates.NextWeek(Wednesday));

    [Fact]
    public void NextWeek_Monday_ReturnsSevenDaysLater() =>
        Assert.Equal(new DateOnly(2026, 10, 5), QuickDates.NextWeek(new DateOnly(2026, 9, 28)));

    [Fact]
    public void NextMonth_EndOfJanuary_ReturnsFirstOfFebruary() =>
        Assert.Equal(new DateOnly(2027, 2, 1), QuickDates.NextMonth(new DateOnly(2027, 1, 31)));

    [Fact]
    public void NextMonth_December_ReturnsFirstOfJanuaryNextYear() =>
        Assert.Equal(new DateOnly(2027, 1, 1), QuickDates.NextMonth(new DateOnly(2026, 12, 15)));

    [Fact]
    public void NextBusinessDay_Friday_SkipsWeekend() =>
        Assert.Equal(new DateOnly(2026, 9, 28), QuickDates.NextBusinessDay(new DateOnly(2026, 9, 25), new FixedHolidays()));

    [Fact]
    public void NextBusinessDay_BeforeConsecutiveHolidays_SkipsWeekendAndHolidays()
    {
        var holidays = new FixedHolidays(new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23));

        Assert.Equal(new DateOnly(2026, 9, 24), QuickDates.NextBusinessDay(new DateOnly(2026, 9, 18), holidays));
    }

    [Fact]
    public void NextBusinessDay_Today_IsNeverToday() =>
        Assert.Equal(new DateOnly(2026, 9, 24), QuickDates.NextBusinessDay(Wednesday, new FixedHolidays()));

    [Fact]
    public void Label_Date_ReturnsJapaneseWeekdayAndMonthDay() =>
        Assert.Equal("水 9/23", QuickDates.Label(Wednesday));

    [Fact]
    public void MonthTitle_September_ReturnsYearAndMonth() =>
        Assert.Equal("2026年 9月", QuickDates.MonthTitle(Wednesday));

    [Fact]
    public void MonthGrid_SeptemberSundayStart_CoversAugust30ToOctober3()
    {
        var grid = QuickDates.MonthGrid(Wednesday, DayOfWeek.Sunday);

        Assert.Equal(35, grid.Count);
        Assert.Equal(new DateOnly(2026, 8, 30), grid[0]);
        Assert.Equal(new DateOnly(2026, 10, 3), grid[^1]);
    }

    [Fact]
    public void MonthGrid_MondayStart_StartsOnMondayAndEndsOnSunday()
    {
        var grid = QuickDates.MonthGrid(Wednesday, DayOfWeek.Monday);

        Assert.Equal(new DateOnly(2026, 8, 31), grid[0]);
        Assert.Equal(DayOfWeek.Sunday, grid[^1].DayOfWeek);
        Assert.Equal(0, grid.Count % 7);
        Assert.Contains(new DateOnly(2026, 9, 30), grid);
    }

    [Fact]
    public void MonthGrid_FebruaryStartingOnSunday_IsExactlyFourWeeks()
    {
        // 2026-02-01 は日曜、2月は28日まで
        var grid = QuickDates.MonthGrid(new DateOnly(2026, 2, 10), DayOfWeek.Sunday);

        Assert.Equal(28, grid.Count);
        Assert.Equal(new DateOnly(2026, 2, 1), grid[0]);
        Assert.Equal(new DateOnly(2026, 2, 28), grid[^1]);
    }

    [Fact]
    public void MonthGrid_AnyMonth_DaysAreConsecutive()
    {
        var grid = QuickDates.MonthGrid(new DateOnly(2028, 2, 29), DayOfWeek.Monday);

        for (var i = 1; i < grid.Count; i++)
        {
            Assert.Equal(grid[i - 1].AddDays(1), grid[i]);
        }
    }

    [Fact]
    public void WeekDays_MondayStart_EndsWithSunday()
    {
        var days = QuickDates.WeekDays(DayOfWeek.Monday);

        Assert.Equal(7, days.Count);
        Assert.Equal(DayOfWeek.Monday, days[0]);
        Assert.Equal(DayOfWeek.Sunday, days[^1]);
    }

    [Fact]
    public void DayOfWeekName_Sunday_ReturnsJapaneseShortName() =>
        Assert.Equal("日", QuickDates.DayOfWeekName(DayOfWeek.Sunday));
}

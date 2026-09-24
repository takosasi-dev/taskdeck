using System.Globalization;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;

namespace TaskDeck.App.Controls.Pickers;

/// <summary>
/// 日付ピッカーの日付の計算（UI 設計書 12.1、F-194）。担当: 波1-D。
/// 「来週」が何日かで迷わせないため、画面には必ずここで出した実日付を併記する。
/// 純粋な計算だけを置く（今日と祝日は呼ぶ側が渡す）。
/// </summary>
public static class QuickDates
{
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    /// <summary>今週末＝今日を含めて次の土曜（土曜なら当日、日曜なら6日後）。</summary>
    public static DateOnly ThisWeekend(DateOnly today) =>
        today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7);

    /// <summary>来週＝次の月曜（今日が月曜なら7日後）。</summary>
    public static DateOnly NextWeek(DateOnly today)
    {
        var days = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }

    /// <summary>来月＝翌月の1日。</summary>
    public static DateOnly NextMonth(DateOnly today) => new DateOnly(today.Year, today.Month, 1).AddMonths(1);

    /// <summary>次の営業日＝明日以降で最初の土日・祝日でない日（F-194）。</summary>
    public static DateOnly NextBusinessDay(DateOnly today, IHolidayProvider holidays) =>
        BusinessDays.Next(today, holidays);

    /// <summary>クイック選択の右端に出す表記（「水 9/23」）。</summary>
    public static string Label(DateOnly date) => date.ToString("ddd M/d", Japanese);

    /// <summary>月カレンダーの見出し（「2026年 9月」）。</summary>
    public static string MonthTitle(DateOnly anyDayInMonth) =>
        anyDayInMonth.ToString("yyyy年 M月", CultureInfo.InvariantCulture);

    /// <summary>曜日の見出し（週の開始曜日から7つ。「日 月 火 …」）。</summary>
    public static IReadOnlyList<DayOfWeek> WeekDays(DayOfWeek firstDayOfWeek) =>
        [.. Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)firstDayOfWeek + i) % 7))];

    public static string DayOfWeekName(DayOfWeek day) => Japanese.DateTimeFormat.GetShortestDayName(day);

    /// <summary>
    /// 月カレンダーに並べる日（その月を含む週をすべて。前後の月の日も含む、7の倍数個）。
    /// 週の開始曜日は設定（Calendar.WeekStartsOnMonday）に従う。
    /// </summary>
    public static IReadOnlyList<DateOnly> MonthGrid(DateOnly anyDayInMonth, DayOfWeek firstDayOfWeek)
    {
        var first = new DateOnly(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var start = first.AddDays(-(((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        var lastWeekday = (DayOfWeek)(((int)firstDayOfWeek + 6) % 7);
        var end = last.AddDays(((int)lastWeekday - (int)last.DayOfWeek + 7) % 7);
        return [.. Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(start.AddDays)];
    }
}

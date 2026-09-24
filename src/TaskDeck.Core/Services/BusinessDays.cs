using TaskDeck.Core.Abstractions;

namespace TaskDeck.Core.Services;

/// <summary>営業日（土日と祝日以外）。日付ピッカーの「次の営業日」（F-194）と繰り返しの祝日送り（F-193）で使う。</summary>
public static class BusinessDays
{
    public static bool IsBusinessDay(DateOnly date, IHolidayProvider holidays) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.IsHoliday(date);

    /// <summary>date より後の最初の営業日。</summary>
    public static DateOnly Next(DateOnly date, IHolidayProvider holidays) => OnOrAfter(date.AddDays(1), holidays);

    /// <summary>date 当日を含めて、最初の営業日。</summary>
    public static DateOnly OnOrAfter(DateOnly date, IHolidayProvider holidays)
    {
        var d = date;
        // 年末年始の連休でも数日で抜ける。念のため上限を置く（祝日データが壊れていても無限ループしない）
        for (var i = 0; i < 366 && !IsBusinessDay(d, holidays); i++)
        {
            d = d.AddDays(1);
        }
        return d;
    }
}

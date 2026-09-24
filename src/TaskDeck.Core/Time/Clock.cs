namespace TaskDeck.Core.Time;

/// <summary>
/// 現在時刻の唯一の入口。DateTime.Now / Today / UtcNow は禁止（BannedSymbols.txt）。
/// テストでは FixedClock（JST 固定）を注入して「月末」「2/29 の翌年」などを固定値で書く。
/// </summary>
public interface IClock
{
    /// <summary>現在時刻（UTC、Kind=Utc）。</summary>
    DateTime UtcNow { get; }

    /// <summary>日付の判定に使うローカルのタイムゾーン。</summary>
    TimeZoneInfo LocalTimeZone { get; }
}

public sealed class SystemClock : IClock
{
#pragma warning disable RS0030 // 禁止APIの唯一の例外（時刻とタイムゾーンの取得元）
    public DateTime UtcNow => DateTime.UtcNow;
    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
#pragma warning restore RS0030
}

/// <summary>ローカル日付との変換。表示・日付の比較はこれを通す（ToLocalTime / ToUniversalTime は禁止）。</summary>
public static class ClockExtensions
{
    /// <summary>ローカルの現在時刻（Kind=Unspecified）。</summary>
    public static DateTime LocalNow(this IClock clock) => clock.ToLocal(clock.UtcNow);

    public static DateOnly LocalToday(this IClock clock) => DateOnly.FromDateTime(clock.LocalNow());

    /// <summary>UTC をローカルの壁時計時刻に直す（Kind=Unspecified）。</summary>
    public static DateTime ToLocal(this IClock clock, DateTime utc)
    {
        var u = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(u, clock.LocalTimeZone), DateTimeKind.Unspecified);
    }

    /// <summary>ローカルの壁時計時刻を UTC に直す（Kind=Utc）。存在しない時刻（夏時間の飛び）は1時間後ろへ寄せる。</summary>
    public static DateTime ToUtc(this IClock clock, DateTime local)
    {
        var l = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (clock.LocalTimeZone.IsInvalidTime(l))
        {
            l = l.AddHours(1);
        }
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(l, clock.LocalTimeZone), DateTimeKind.Utc);
    }

    /// <summary>ローカルの日付の 0:00 を UTC で。日付のみの期限はこの値で保存する。</summary>
    public static DateTime LocalDayStartUtc(this IClock clock, DateOnly day) =>
        clock.ToUtc(day.ToDateTime(TimeOnly.MinValue));

    /// <summary>UTC の日時がローカルで何日か。</summary>
    public static DateOnly ToLocalDate(this IClock clock, DateTime utc) => DateOnly.FromDateTime(clock.ToLocal(utc));

    /// <summary>ローカルの日付と時刻から UTC を作る。</summary>
    public static DateTime LocalToUtc(this IClock clock, DateOnly day, TimeOnly time) =>
        clock.ToUtc(day.ToDateTime(time));
}

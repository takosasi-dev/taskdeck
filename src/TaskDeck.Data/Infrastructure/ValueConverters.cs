using System.Globalization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TaskDeck.Data.Infrastructure;

/// <summary>
/// DB の日時は UTC。読み出した値に Kind=Utc を付け、書き込む値も Utc として扱う。
/// Kind=Local の値が来たら UTC に直す（本来 IClock を通すべきなので、来ないのが正しい）。
/// </summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => ToUtc(v),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    private static DateTime ToUtc(DateTime value)
    {
#pragma warning disable RS0030 // 変換器だけは Kind=Local の値を受けたときの保険として使う
        return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
#pragma warning restore RS0030
    }
}

/// <summary>GUID の一覧をカンマ区切りの文字列で持つ（テンプレートの既定タグ・項目のタグ）。</summary>
public sealed class GuidListConverter() : ValueConverter<List<Guid>, string>(
    v => string.Join(',', v.Select(g => g.ToString("D"))),
    v => Parse(v))
{
    /// <summary>コンパイル済みモデル（CompiledModels）が変換の式をそのまま書き写して呼ぶので internal。</summary>
    internal static List<Guid> Parse(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();
}

public sealed class GuidListComparer() : ValueComparer<List<Guid>>(
    (a, b) => a != null && b != null ? a.SequenceEqual(b) : a == b,
    v => v.Aggregate(0, (h, g) => HashCode.Combine(h, g.GetHashCode())),
    v => v.ToList());

/// <summary>時刻を "HH:mm" の文字列で持つ（テンプレート項目の DueTime）。</summary>
public sealed class HourMinuteConverter() : ValueConverter<TimeOnly, string>(
    v => v.ToString("HH:mm", CultureInfo.InvariantCulture),
    v => TimeOnly.ParseExact(v, "HH:mm", CultureInfo.InvariantCulture));

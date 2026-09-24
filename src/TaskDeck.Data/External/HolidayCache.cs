using Microsoft.EntityFrameworkCore;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Settings;

namespace TaskDeck.Data.External;

/// <summary>
/// 祝日の問い合わせ（IHolidayProvider）。Holiday 表をメモリに読み込んで即答する。取得前・失敗時は「祝日なし」として答える。
/// 設定の「日本の祝日を取得する」が OFF の間も「祝日なし」（取ってあった分も使わない。カレンダー・次の営業日・平日の繰り返しに効く）。
/// オフラインモードでは取ってあった分を使う（通信しないだけ）。取得は <see cref="HolidayUpdater"/>。
/// </summary>
public sealed class HolidayCache(IDbContextFactory<TaskDeckDbContext> factory, ISettingsStore? settings) : IHolidayProvider
{
    private volatile IReadOnlyDictionary<DateOnly, string> _names = new Dictionary<DateOnly, string>();

    /// <summary>設定を見ない版（常に表の中身で答える）。</summary>
    public HolidayCache(IDbContextFactory<TaskDeckDbContext> factory)
        : this(factory, null)
    {
    }

    private bool Enabled => settings?.Current.External.HolidaysEnabled ?? true;

    /// <summary>Holiday 表を読み直す。起動時と取得後に呼ぶ。</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Holidays.AsNoTracking().ToListAsync(ct);
        _names = rows.ToDictionary(h => h.Date, h => h.LocalName);
    }

    public bool IsHoliday(DateOnly date) => Enabled && _names.ContainsKey(date);

    public string? GetName(DateOnly date) => Enabled ? _names.GetValueOrDefault(date) : null;
}

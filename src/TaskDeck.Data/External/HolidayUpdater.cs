using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.External;

/// <summary>
/// 祝日の取得（F-191）。内閣府の「国民の祝日」CSV（syukujitsu.csv、1955年〜翌年）を1日1回まで取りに行く
/// （依頼者の決定。Nager.Date は振替休日を元の祝日の日付ごと動かし、国民の休日を返さないため）。送るものは無い（ファイルを取るだけ）。
/// 取れたら Holiday 表を丸ごと入れ替え、HolidayCache を読み直して DataChangeHub に ExternalCache を出す。
/// 取った日時と年の範囲は AppStateKeys.HolidayFetchedYears に持つ。形が想定と違えば何も書かずに捨てる（次の機会に取り直す）。
/// </summary>
public sealed class HolidayUpdater(
    ExternalHttp http,
    IDbContextFactory<TaskDeckDbContext> factory,
    HolidayCache cache,
    DataChangeHub hub,
    ISettingsStore settings,
    IClock clock,
    ILogger<HolidayUpdater> logger)
{
    public const string CountryCode = "JP";

    public static readonly Uri SourceUri = new("https://www8.cao.go.jp/chosei/shukujitsu/syukujitsu.csv");

    /// <summary>AppState に残す取得の記録。Source が違う（Nager.Date の頃の記録など）なら、取っていないものとして取り直す。</summary>
    private sealed record FetchRecord(string Source, DateTime FetchedAt, int FirstYear, int LastYear);

    private const string Source = "cao";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 今日まだ取っていなければ取る（force なら今日取っていても取り直す）。設定が OFF なら何もしない。取れたら 1、取らなかった・取れなかったら 0。
    /// </summary>
    public async Task<int> RefreshAsync(bool force, CancellationToken ct = default)
    {
        if (!settings.Current.External.HolidaysEnabled)
        {
            return 0;
        }
        await _gate.WaitAsync(ct);
        try
        {
            var record = await ReadRecordAsync(ct);
            if (!force && record is not null && clock.ToLocalDate(record.FetchedAt) == clock.LocalToday())
            {
                return 0;
            }
            var content = await http.GetBytesAsync(ExternalApi.Holidays, SourceUri, ct);
            if (content is null)
            {
                return 0;
            }
            var holidays = Parse(content, clock.LocalToday().Year);
            if (holidays is null)
            {
                logger.LogWarning("祝日の CSV が想定外の形だったので使いませんでした（{Bytes} バイト）", content.Length);
                return 0;
            }
            var fetched = new FetchRecord(Source, clock.UtcNow, holidays[0].Date.Year, holidays[^1].Date.Year);
            await ReplaceAllAsync(holidays, fetched, ct);
            logger.LogInformation("祝日を取得しました（{First}〜{Last} 年・{Count} 件）", fetched.FirstYear, fetched.LastYear, holidays.Count);
            await cache.ReloadAsync(ct);
            hub.Publish(DataChangeKind.ExternalCache);
            return 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>最後に取った日時（UTC）と、取ってある年（古い順）。まだ何も取っていなければ (null, 空)。</summary>
    public async Task<(DateTime? LastFetchedAt, IReadOnlyList<int> Years)> GetStatusAsync(CancellationToken ct = default)
    {
        var record = await ReadRecordAsync(ct);
        return record is null
            ? (null, [])
            : (record.FetchedAt, [.. Enumerable.Range(record.FirstYear, record.LastYear - record.FirstYear + 1)]);
    }

    /// <summary>
    /// 内閣府の CSV（1行目は見出し、以降「2026/5/6,休日」の形）を検査して、日付順の Holiday にする。
    /// 文字コードは Shift_JIS（UTF-8 に変わっても読めるよう、UTF-8 として正しければそちらで読む）。
    /// 読めない行がある・thisYear の祝日が1件も無い（古い・途切れたファイル）ときは null（全部捨てる）。同じ日が2件あれば名前を「・」でつなぐ。
    /// </summary>
    internal static IReadOnlyList<Holiday>? Parse(byte[] content, int thisYear)
    {
        var names = new SortedDictionary<DateOnly, string>();
        var lines = Decode(content).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var parts = line.Split(',');
            if (parts.Length != 2
                || !DateOnly.TryParseExact(parts[0].Trim(), "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || parts[1].Trim() is not { Length: > 0 } name)
            {
                if (i == 0)
                {
                    continue;   // 見出し
                }
                return null;
            }
            names[date] = names.TryGetValue(date, out var existing) ? existing + "・" + name : name;
        }
        if (!names.Keys.Any(d => d.Year == thisYear))
        {
            return null;
        }
        return
        [
            .. names.Select(pair => new Holiday
            {
                Date = pair.Key,
                LocalName = pair.Value.Length > 100 ? pair.Value[..100] : pair.Value,
                CountryCode = CountryCode,
            }),
        ];
    }

    private static string Decode(byte[] content)
    {
        try
        {
            return StrictUtf8.GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(932)!.GetString(content);
        }
    }

    private async Task<FetchRecord?> ReadRecordAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = await db.AppState.AsNoTracking()
            .Where(s => s.Key == AppStateKeys.HolidayFetchedYears)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var record = JsonSerializer.Deserialize<FetchRecord>(json);
            return record is { Source: Source } && record.FirstYear <= record.LastYear
                ? record with { FetchedAt = DateTime.SpecifyKind(record.FetchedAt, DateTimeKind.Utc) }
                : null;
        }
        catch (JsonException ex)
        {
            // 壊れていたら「まだ取っていない」として取り直す
            logger.LogWarning(ex, "祝日を取った記録が読めなかったので、取り直します");
            return null;
        }
    }

    /// <summary>Holiday 表を丸ごと入れ替え、取った記録も同じトランザクションで書く。</summary>
    private async Task ReplaceAllAsync(IReadOnlyList<Holiday> holidays, FetchRecord fetched, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(fetched);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Holidays.ExecuteDeleteAsync(ct);
        db.Holidays.AddRange(holidays);
        var state = await db.AppState.FirstOrDefaultAsync(s => s.Key == AppStateKeys.HolidayFetchedYears, ct);
        if (state is null)
        {
            db.AppState.Add(new AppStateEntry { Key = AppStateKeys.HolidayFetchedYears, Value = json });
        }
        else
        {
            state.Value = json;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

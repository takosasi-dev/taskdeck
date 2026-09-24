using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.External;

/// <summary>メモの URL 1件と、そのページのタイトル（取れていなければ null）。</summary>
public sealed record LinkPreviewItem(string Url, string? Title);

/// <summary>
/// メモの URL のタイトル（F-196、Microlink。設計書 4.7.5）。<b>既定 OFF</b>。
/// 送るのはメモから拾った URL だけで、設定が ON かつオフラインでないときだけ送る（NFR 6.4）。
/// 取ったものは LinkPreview 表に持ち、30日で取り直す（取れなかったものは1日おいてから）。
/// 1つのメモから拾う URL は先頭の5件まで（無料枠を食いつぶさないため）。
/// </summary>
public sealed partial class LinkPreviewService(
    ExternalHttp http,
    IDbContextFactory<TaskDeckDbContext> factory,
    ISettingsStore settings,
    IClock clock,
    ILogger<LinkPreviewService> logger)
{
    public const int MaxUrls = 5;
    public const int MaxUrlLength = 2048;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>設定が ON で、オフラインでない（タイトルを出してよい）。</summary>
    public bool IsActive => settings.Current.External is { LinkPreviewEnabled: true, OfflineMode: false };

    public static Uri RequestUri(string url) => new("https://api.microlink.io/?url=" + Uri.EscapeDataString(url));

    /// <summary>
    /// メモから http/https の URL を拾う（先頭から5件、重複なし）。日本語の文にくっついた URL も、URL に使える文字の所までで切る。
    /// 末尾の句読点と、対になっていない閉じかっこは外す。
    /// </summary>
    public static IReadOnlyList<string> ExtractUrls(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var urls = new List<string>();
        foreach (Match match in UrlPattern().Matches(text))
        {
            var url = TrimTail(match.Value);
            if (url.Length > MaxUrlLength
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host)
                || urls.Contains(url, StringComparer.Ordinal))
            {
                continue;
            }
            urls.Add(url);
            if (urls.Count == MaxUrls)
            {
                break;
            }
        }
        return urls;
    }

    /// <summary>
    /// URL ごとのタイトル。キャッシュに無いもの・古いものは、有効（<see cref="IsActive"/>）なら取ってから返す。
    /// 取れなかったものは Title=null（画面は URL をそのまま出す）。並びは urls のまま。
    /// </summary>
    public async Task<IReadOnlyList<LinkPreviewItem>> ResolveAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        if (urls.Count == 0)
        {
            return [];
        }
        var cached = await ReadAsync(urls, ct);
        var now = clock.UtcNow;
        var stale = urls.Where(url => !cached.TryGetValue(url, out var row) || IsStale(row, now)).ToList();
        if (stale.Count > 0 && IsActive)
        {
            var fetched = await Task.WhenAll(stale.Select(url => FetchAsync(url, ct)));
            var rows = fetched.OfType<LinkPreview>().ToList();
            await SaveAsync(rows, ct);
            foreach (var row in rows)
            {
                cached[row.Url] = row;
            }
        }
        return [.. urls.Select(url => new LinkPreviewItem(url, cached.TryGetValue(url, out var row) ? row.Title : null))];
    }

    /// <summary>{"status":"success","data":{"title":"...","description":"..."}} からタイトルと説明。形が違えば null。</summary>
    internal static (string? Title, string? Description)? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
            || status.GetString() != "success"
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return (Text(data, "title", 500), Text(data, "description", 1000));
    }

    private static bool IsStale(LinkPreview row, DateTime now) =>
        now - row.FetchedAt >= (row.Title is null ? RetryAfterFailure : MaxAge);

    /// <summary>1件取る。通信しなかった（オフラインにされた・429 で止めた）なら null（記録しない）。</summary>
    private async Task<LinkPreview?> FetchAsync(string url, CancellationToken ct)
    {
        var root = await http.GetJsonAsync(ExternalApi.LinkPreview, RequestUri(url), ct);
        if (root is null && (!IsActive || http.IsPaused(ExternalApi.LinkPreview)))
        {
            return null;
        }
        var parsed = root is { } r ? Parse(r) : null;
        if (root is not null && parsed is null)
        {
            logger.LogWarning("URL のタイトルの応答が想定外の形だったので使いませんでした");
        }
        // 取れなかったものも記録して、1日は取り直さない（無料枠を同じ URL で使い切らないため）
        return new LinkPreview
        {
            Url = url,
            Title = parsed?.Title,
            Description = parsed?.Description,
            FetchedAt = clock.UtcNow,
        };
    }

    private async Task<Dictionary<string, LinkPreview>> ReadAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.LinkPreviews.AsNoTracking().Where(p => urls.Contains(p.Url)).ToListAsync(ct);
        return rows.ToDictionary(p => p.Url, StringComparer.Ordinal);
    }

    private async Task SaveAsync(IReadOnlyList<LinkPreview> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }
        // 同じメモを続けて開いたときに同じ URL を2回足さないよう、書き込みは1つずつ
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var urls = rows.Select(r => r.Url).ToList();
            var existing = await db.LinkPreviews.Where(p => urls.Contains(p.Url)).ToDictionaryAsync(p => p.Url, StringComparer.Ordinal, ct);
            foreach (var row in rows)
            {
                if (existing.TryGetValue(row.Url, out var current))
                {
                    current.Title = row.Title;
                    current.Description = row.Description;
                    current.FetchedAt = row.FetchedAt;
                }
                else
                {
                    db.LinkPreviews.Add(row);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string TrimTail(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or '*')
            {
                url = url[..^1];
            }
            else if (last == ')' && url.Count(c => c == ')') > url.Count(c => c == '('))
            {
                url = url[..^1];
            }
            else if (last == ']' && url.Count(c => c == ']') > url.Count(c => c == '['))
            {
                url = url[..^1];
            }
            else
            {
                break;
            }
        }
        return url;
    }

    private static string? Text(JsonElement parent, string name, int maxLength) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()?.Trim() is { Length: > 0 } text
            ? (text.Length > maxLength ? text[..maxLength] : text)
            : null;

    /// <summary>URL に使える文字（RFC 3986）だけで作った並び。日本語の文字で切れる。</summary>
    [GeneratedRegex(@"https?://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}

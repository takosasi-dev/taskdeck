using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.External;

/// <summary>外部API の種類。429 で止めるのは種類ごと（1つが止まっても他は動く）。</summary>
public enum ExternalApi
{
    /// <summary>内閣府の「国民の祝日」CSV（祝日）。</summary>
    Holidays,

    /// <summary>Open-Meteo の予報（天気）。</summary>
    Weather,

    /// <summary>Open-Meteo の地名検索（天気の場所）。</summary>
    Geocoding,

    /// <summary>Microlink（URL のタイトル）。</summary>
    LinkPreview,

    /// <summary>GitHub Releases（更新の確認）。</summary>
    Releases,
}

/// <summary>
/// 外部API の GET。通信の決まり（CLAUDE.md 1.6・NFR 6.3/6.4・INTERFACES 5.8）はここにだけ書く:
/// - IHttpClientFactory の名前付きクライアント（<see cref="ClientName"/>）を使う。HTTPS だけ（それ以外の URL は作った側の誤りなので例外）。
///   証明書の検証・プロキシは既定のまま（システムの設定に従う）
/// - オフラインモードなら通信しない。429 が返ったら、その API を1時間止める（メモリだけに持つ）
/// - 1回5秒で打ち切る。通信の失敗・打ち切り・5xx なら2秒後に1回だけやり直す（4xx と壊れた JSON はやり直さない）
/// - 失敗は静かに: null を返し、ログは Warning まで。URL とクエリはログに出さない（メモの URL・入力した地名が入るため）
/// - 返った JSON の形の検査は呼ぶ側（API ごとのクラス）がする
/// </summary>
public sealed class ExternalHttp(IHttpClientFactory clients, ISettingsStore settings, IClock clock, ILogger<ExternalHttp> logger)
{
    public const string ClientName = "TaskDeck.External";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RateLimitPause = TimeSpan.FromHours(1);

    /// <summary>名乗り（GitHub API では必須、他でも礼儀として付ける）。端末やユーザーを特定できる情報は入れない。</summary>
    public static readonly string UserAgent = "TaskDeck/" + (typeof(ExternalHttp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    private readonly ConcurrentDictionary<ExternalApi, DateTime> _pausedUntil = new();

    /// <summary>1回の通信の打ち切り時間（テストで短くする）。</summary>
    internal TimeSpan RequestTimeout { get; init; } = DefaultTimeout;

    /// <summary>やり直す前の待ち時間。</summary>
    internal TimeSpan RetryDelay { get; init; } = DefaultRetryDelay;

    /// <summary>待ち方（テストでは待たずに、待った時間を記録する）。</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>429 で止めている間は true。</summary>
    public bool IsPaused(ExternalApi api) => _pausedUntil.TryGetValue(api, out var until) && clock.UtcNow < until;

    /// <summary>
    /// GET して JSON の根を返す（中身は呼び出し側で検査する）。通信しなかった・できなかった・JSON でなかったときは null。
    /// 呼び出し側の取り消し（ct）だけは OperationCanceledException で返す。
    /// </summary>
    public Task<JsonElement?> GetJsonAsync(ExternalApi api, Uri uri, CancellationToken ct = default) =>
        GetAsync(api, uri, "application/json", ReadJsonAsync, ct);

    /// <summary>GET して中身をそのまま返す（CSV など JSON でないもの。文字コードの解釈と形の検査は呼び出し側）。決まりは GetJsonAsync と同じ。</summary>
    public Task<byte[]?> GetBytesAsync(ExternalApi api, Uri uri, CancellationToken ct = default) =>
        GetAsync<byte[]?>(api, uri, "*/*", async (content, token) => await content.ReadAsByteArrayAsync(token), ct);

    private async Task<T?> GetAsync<T>(ExternalApi api, Uri uri, string accept, Func<HttpContent, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("外部への通信は HTTPS だけです（NFR 6.3）", nameof(uri));
        }

        for (var attempt = 1; ; attempt++)
        {
            // やり直しの前にも見る（待っている間にオフラインにされたら送らない）
            if (settings.Current.External.OfflineMode || IsPaused(api))
            {
                return default;
            }
            var (result, ok, retry) = await SendOnceAsync(api, uri, accept, read, ct);
            if (ok || !retry || attempt >= 2)
            {
                return result;
            }
            await Delay(RetryDelay, ct);
        }
    }

    private static async Task<JsonElement?> ReadJsonAsync(HttpContent content, CancellationToken ct)
    {
        await using var body = await content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    /// <summary>1回だけ送る。Retry は「やり直せば通るかもしれない失敗」（通信の失敗・打ち切り・5xx・408）。</summary>
    private async Task<(T? Result, bool Ok, bool Retry)> SendOnceAsync<T>(
        ExternalApi api, Uri uri, string accept, Func<HttpContent, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _pausedUntil[api] = clock.UtcNow + RateLimitPause;
                logger.LogWarning("外部API {Api} が混み合っている（429）ため、1時間呼ぶのを止めます", api);
                return (default, false, false);
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var retry = status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
                logger.LogWarning("外部API {Api} が HTTP {Status} を返しました", api, status);
                return (default, false, retry);
            }

            return (await read(response.Content, timeout.Token), true, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("外部API {Api} が {Seconds} 秒以内に応えませんでした", api, RequestTimeout.TotalSeconds);
            return (default, false, true);
        }
        catch (HttpRequestException ex)
        {
            // 名前解決・接続・TLS の失敗と、大きすぎる応答（MaxResponseContentBufferSize）。メッセージにはホスト名しか入らない
            logger.LogWarning(ex, "外部API {Api} に接続できませんでした", api);
            return (default, false, true);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("外部API {Api} の応答が JSON ではありませんでした（{Error}）", api, ex.GetType().Name);
            return (default, false, false);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "外部API {Api} の応答を読み切れませんでした", api);
            return (default, false, true);
        }
    }
}

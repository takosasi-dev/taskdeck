using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TaskDeck.App.BackgroundTasks;

/// <summary>
/// 開発用: Microlink と GitHub Releases への要求を、外に出さずにその場で答える（担当: 波3-K）。
/// 開発用データフォルダ（TASKDECK_DATA_DIR）で起動し、さらに環境変数 TASKDECK_DEV_INSIGHTS があるときだけ働く。本番では何もしない。
/// メモの URL を本物の Microlink に送らずに「URL のタイトル」の表示を、更新の確認の「新しい版があります」を本物の GitHub に聞かずに確かめるため。
/// 祝日・天気（内閣府の CSV・Open-Meteo）はそのまま本物へ通す。
/// </summary>
internal sealed class DevFakeExternalHandler : DelegatingHandler
{
    public const string Variable = "TASKDECK_DEV_INSIGHTS";

    /// <summary>働いているときだけ、更新の確認に使う仮のリポジトリ名（答えるのはこの偽物だけ）。</summary>
    public static string? Repository => IsRequested ? "example/taskdeck-dev" : null;

    private static bool IsRequested =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirVariable))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    public static void AttachIfRequested(IHttpClientBuilder builder)
    {
        if (IsRequested)
        {
            builder.AddHttpMessageHandler(() => new DevFakeExternalHandler());
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "api.microlink.io")
        {
            var target = System.Web.HttpUtility.ParseQueryString(uri.Query)["url"] ?? "";
            var host = Uri.TryCreate(target, UriKind.Absolute, out var parsed) ? parsed.Host : "?";
            return Json(new { status = "success", data = new { title = $"（開発用の偽タイトル）{host} のページ", description = (string?)null } });
        }
        if (uri.Host == "api.github.com")
        {
            return Json(new { tag_name = "v9.9.9", html_url = "https://github.com/example/taskdeck-dev/releases/tag/v9.9.9" });
        }
        return base.SendAsync(request, cancellationToken);
    }

    private static Task<HttpResponseMessage> Json(object body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
}

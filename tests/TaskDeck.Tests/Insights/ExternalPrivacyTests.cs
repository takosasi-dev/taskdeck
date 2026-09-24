using System.Globalization;
using System.Net.Http;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Data.External;
using TaskDeck.Data.Repositories;

namespace TaskDeck.Tests.Insights;

/// <summary>
/// 外に送るもの（NFR 6.4）: 丸めた緯度経度・入力した地名・メモの URL（ON のときだけ）・リポジトリ名だけ（祝日は決まったファイルを取るだけで何も送らない）。
/// タスクのタイトル・メモの中身・プロジェクト名・タグ名は、どの要求にも入らない。
/// </summary>
public sealed class ExternalPrivacyTests : IDisposable
{
    private const string SecretTitle = "取引先A社の見積りを出す";
    private const string SecretNotes = "担当は山田さん。社外秘 https://intra.example.com/doc?id=42 を参照";
    private const string SecretProject = "極秘プロジェクト";
    private const string SecretTag = "人事";

    private readonly ExternalWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task AllApis_SendOnlyAllowedValues()
    {
        // タスクの中身がある DB で、全部の API を動かす
        var tasks = new TaskRepository(_world.Db, _world.Clock, Substitute.For<IRecurrenceEngine>(), _world.Settings, _world.Hub, NullLogger<TaskRepository>.Instance);
        await tasks.AddAsync(new NewTaskRequest { Title = SecretTitle, Notes = SecretNotes, ProjectName = SecretProject, TagNames = [SecretTag] });

        var external = _world.Settings.Current.External;
        external.WeatherEnabled = true;
        external.WeatherLocation = new WeatherLocation("大阪市（大阪府・日本）", 34.693738, 135.502165);
        external.LinkPreviewEnabled = true;
        _world.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.Host == "www8.cao.go.jp"
            ? FakeExternalHandler.Csv(ExternalWorld.HolidaysCsv())
            : FakeExternalHandler.Json(request.RequestUri.Host switch
        {
            "api.open-meteo.com" => ExternalWorld.WeatherJson,
            "geocoding-api.open-meteo.com" => """{"results":[]}""",
            "api.microlink.io" => """{"status":"success","data":{"title":"t"}}""",
            _ => """{"tag_name":"v0.1.0"}""",
        }));

        await _world.NewHolidayUpdater().RefreshAsync(force: true);
        await _world.NewWeatherUpdater().RefreshAsync(force: true);
        await _world.NewPlaceSearch().SearchAsync("大阪");
        await _world.NewLinkPreviewService().ResolveAsync(LinkPreviewService.ExtractUrls(SecretNotes));
        await _world.NewUpdateChecker("owner/taskdeck").CheckAsync(force: true);

        Assert.Equal(5, _world.Handler.Requests.Count);
        foreach (var request in _world.Handler.Requests)
        {
            var uri = request.RequestUri!;
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);

            // タスクの中身は、URL にもヘッダにも入らない（URL はメモから拾ったものだけ）
            var everything = Uri.UnescapeDataString(uri.AbsoluteUri) + "\n" + request.Headers;
            foreach (var secret in new[] { SecretTitle, "山田", "社外秘", SecretProject, SecretTag, "大阪市" })
            {
                Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            var keys = query.AllKeys.OfType<string>().ToHashSet();
            switch (uri.Host)
            {
                case "www8.cao.go.jp":
                    Assert.Equal(ExternalWorld.HolidaysCsvUri, uri.AbsoluteUri);
                    break;
                case "api.open-meteo.com":
                    Assert.True(keys.SetEquals(["latitude", "longitude", "daily", "timezone", "forecast_days"]));
                    Assert.Equal("34.69", query["latitude"]);
                    Assert.Equal("135.50", query["longitude"]);
                    Assert.Equal("auto", query["timezone"]);
                    break;
                case "geocoding-api.open-meteo.com":
                    Assert.Equal("大阪", query["name"]);
                    Assert.True(keys.SetEquals(["name", "count", "language", "format"]));
                    break;
                case "api.microlink.io":
                    Assert.Equal(["url"], keys);
                    Assert.Equal("https://intra.example.com/doc?id=42", query["url"]);
                    break;
                case "api.github.com":
                    Assert.Equal("/repos/owner/taskdeck/releases/latest", uri.AbsolutePath);
                    Assert.Empty(keys);
                    break;
                default:
                    Assert.Fail($"想定していない送り先: {uri.Host}");
                    break;
            }
        }
    }
}

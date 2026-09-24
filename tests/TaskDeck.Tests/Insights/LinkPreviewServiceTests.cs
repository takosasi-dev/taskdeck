using System.Net;
using Microsoft.EntityFrameworkCore;
using TaskDeck.Data.External;

namespace TaskDeck.Tests.Insights;

/// <summary>URL のタイトル（Microlink、既定 OFF）: ON かつオフラインでないときだけ、メモの URL だけを送る。30日キャッシュ。</summary>
public sealed class LinkPreviewServiceTests : IDisposable
{
    private readonly ExternalWorld _world = new();

    public LinkPreviewServiceTests()
    {
        _world.Settings.Current.External.LinkPreviewEnabled = true;
        _world.Handler.Respond = (request, _) =>
        {
            var target = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["url"];
            return Task.FromResult(FakeExternalHandler.Json(
                $$"""{"status":"success","data":{"title":"タイトル {{target}}","description":"説明"},"statusCode":200}"""));
        };
    }

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("詳細はhttps://example.com/a?b=1を見る", "https://example.com/a?b=1")]
    [InlineData("（https://example.com/page）参照", "https://example.com/page")]
    [InlineData("see https://en.wikipedia.org/wiki/Foo_(bar).", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("(https://example.com/x), next", "https://example.com/x")]
    [InlineData("http://example.com/old", "http://example.com/old")]
    public void ExtractUrls_Text_FindsUrlWithoutSurroundingText(string text, string expected) =>
        Assert.Equal([expected], LinkPreviewService.ExtractUrls(text));

    [Fact]
    public void ExtractUrls_ManyAndDuplicated_TakesFirstFiveDistinctHttpOnly()
    {
        var text = string.Join("\n", Enumerable.Range(1, 7).Select(i => $"https://example.com/{i}"))
            + "\nhttps://example.com/1\nftp://example.com/f\njavascript:alert(1)\nfile:///C:/x";

        var urls = LinkPreviewService.ExtractUrls(text);

        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"https://example.com/{i}"), urls);
    }

    [Fact]
    public void ExtractUrls_NoUrl_ReturnsEmpty()
    {
        Assert.Empty(LinkPreviewService.ExtractUrls(null));
        Assert.Empty(LinkPreviewService.ExtractUrls("URL の無いメモ https:// だけ"));
    }

    [Fact]
    public async Task ResolveAsync_Enabled_SendsOnlyTheUrlAndCachesTitle()
    {
        var service = _world.NewLinkPreviewService();

        var items = await service.ResolveAsync(["https://example.com/a?b=1&c=2"]);

        var uri = Assert.Single(_world.Handler.Uris);
        Assert.Equal("https://api.microlink.io/?url=https%3A%2F%2Fexample.com%2Fa%3Fb%3D1%26c%3D2", uri.AbsoluteUri);
        Assert.Equal("タイトル https://example.com/a?b=1&c=2", Assert.Single(items).Title);

        // 30日の間は取り直さない
        _world.Handler.Requests.Clear();
        _world.Clock.Advance(TimeSpan.FromDays(29));
        Assert.NotNull(Assert.Single(await service.ResolveAsync(["https://example.com/a?b=1&c=2"])).Title);
        Assert.Empty(_world.Handler.Requests);

        _world.Clock.Advance(TimeSpan.FromDays(1));
        await service.ResolveAsync(["https://example.com/a?b=1&c=2"]);
        Assert.Single(_world.Handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_DefaultSettings_SendsNothing()
    {
        _world.Settings.Current.External.LinkPreviewEnabled = false;   // 既定は OFF

        var items = await _world.NewLinkPreviewService().ResolveAsync(["https://example.com/"]);

        Assert.Null(Assert.Single(items).Title);
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_Offline_SendsNothing()
    {
        _world.Settings.Current.External.OfflineMode = true;

        var service = _world.NewLinkPreviewService();
        await service.ResolveAsync(["https://example.com/"]);

        Assert.False(service.IsActive);
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_Failure_ShowsUrlAndWaitsOneDayBeforeRetry()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json(
            """{"status":"fail","code":"EINVALURL","message":"The URL is not reachable."}""", HttpStatusCode.BadRequest));
        var service = _world.NewLinkPreviewService();

        Assert.Null(Assert.Single(await service.ResolveAsync(["https://unreachable.example/"])).Title);
        Assert.Single(_world.Handler.Requests);

        _world.Clock.Advance(TimeSpan.FromHours(23));
        await service.ResolveAsync(["https://unreachable.example/"]);
        Assert.Single(_world.Handler.Requests);

        _world.Clock.Advance(TimeSpan.FromHours(1));
        await service.ResolveAsync(["https://unreachable.example/"]);
        Assert.Equal(2, _world.Handler.Requests.Count);
    }

    [Fact]
    public async Task ResolveAsync_TooManyRequests_DoesNotRememberFailure()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(HttpStatusCode.TooManyRequests));
        var service = _world.NewLinkPreviewService();

        await service.ResolveAsync(["https://example.com/"]);

        await using var db = _world.Db.CreateDbContext();
        Assert.Equal(0, await db.LinkPreviews.CountAsync());   // 止めただけなので、1時間後にまた取りに行ける
    }

    [Fact]
    public async Task ResolveAsync_SameUrlTwiceAtOnce_StoresOneRow()
    {
        var service = _world.NewLinkPreviewService();

        await Task.WhenAll(service.ResolveAsync(["https://example.com/"]), service.ResolveAsync(["https://example.com/"]));

        await using var db = _world.Db.CreateDbContext();
        Assert.Equal(1, await db.LinkPreviews.CountAsync());
    }
}

using System.Net;
using System.Net.Http;
using TaskDeck.Data.External;

namespace TaskDeck.Tests.Insights;

/// <summary>通信の決まり: 5秒で打ち切り・2秒後に1回だけやり直す・429 で1時間止める・オフラインで送らない・失敗は null（例外にしない）。</summary>
public sealed class ExternalHttpTests : IDisposable
{
    private static readonly Uri Target = new("https://example.test/api");

    private readonly ExternalWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task GetJsonAsync_Success_ReturnsRootWithUserAgentAndNamedClient()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json("""{"ok":true}"""));

        var root = await _world.Http.GetJsonAsync(ExternalApi.Holidays, Target);

        Assert.NotNull(root);
        Assert.True(root.Value.GetProperty("ok").GetBoolean());
        Assert.Single(_world.Handler.Requests);
        Assert.Equal(ExternalHttp.ClientName, Assert.Single(_world.Factory.Names));
        Assert.StartsWith("TaskDeck/", _world.Handler.Requests[0].Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Empty(_world.Delays);
    }

    [Fact]
    public async Task GetJsonAsync_Timeout_RetriesOnceAfterTwoSecondsThenReturnsNull()
    {
        // 打ち切り時間（テストでは 200ms）まで返さない
        _world.Handler.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeExternalHandler.Json("{}");
        };

        var root = await _world.Http.GetJsonAsync(ExternalApi.Weather, Target);

        Assert.Null(root);
        Assert.Equal(2, _world.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], _world.Delays);
    }

    [Fact]
    public async Task GetJsonAsync_ServerError_RetriesOnceAfterTwoSecondsThenReturnsNull()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(HttpStatusCode.InternalServerError));

        var root = await _world.Http.GetJsonAsync(ExternalApi.Weather, Target);

        Assert.Null(root);
        Assert.Equal(2, _world.Handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], _world.Delays);
    }

    [Fact]
    public async Task GetJsonAsync_ServerErrorThenSuccess_ReturnsSecondAnswer()
    {
        _world.Handler.RespondInOrder(
            () => FakeExternalHandler.Status(HttpStatusCode.BadGateway),
            () => FakeExternalHandler.Json("""{"n":2}"""));

        var root = await _world.Http.GetJsonAsync(ExternalApi.Weather, Target);

        Assert.Equal(2, root!.Value.GetProperty("n").GetInt32());
        Assert.Equal(2, _world.Handler.Requests.Count);
    }

    [Fact]
    public async Task GetJsonAsync_ConnectionFailure_RetriesOnceThenReturnsNull()
    {
        _world.Handler.Respond = (_, _) => throw new HttpRequestException("接続できない");

        var root = await _world.Http.GetJsonAsync(ExternalApi.Holidays, Target);

        Assert.Null(root);
        Assert.Equal(2, _world.Handler.Requests.Count);
    }

    [Fact]
    public async Task GetJsonAsync_NotFound_DoesNotRetry()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(HttpStatusCode.NotFound));

        var root = await _world.Http.GetJsonAsync(ExternalApi.Releases, Target);

        Assert.Null(root);
        Assert.Single(_world.Handler.Requests);
        Assert.Empty(_world.Delays);
    }

    [Fact]
    public async Task GetJsonAsync_BrokenJson_ReturnsNullWithoutRetry()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json("""{"date": "2026-01-01", """));

        var root = await _world.Http.GetJsonAsync(ExternalApi.Holidays, Target);

        Assert.Null(root);
        Assert.Single(_world.Handler.Requests);
    }

    [Fact]
    public async Task GetJsonAsync_TooManyRequests_StopsThatApiForOneHour()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(HttpStatusCode.TooManyRequests));

        Assert.Null(await _world.Http.GetJsonAsync(ExternalApi.Weather, Target));
        Assert.Single(_world.Handler.Requests);
        Assert.Empty(_world.Delays);   // 429 はやり直さない
        Assert.True(_world.Http.IsPaused(ExternalApi.Weather));

        // 59分後はまだ送らない。ほかの API は止まっていない
        _world.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Null(await _world.Http.GetJsonAsync(ExternalApi.Weather, Target));
        Assert.Single(_world.Handler.Requests);
        Assert.False(_world.Http.IsPaused(ExternalApi.Holidays));

        // 1時間たったらまた送る
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json("{}"));
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await _world.Http.GetJsonAsync(ExternalApi.Weather, Target));
        Assert.Equal(2, _world.Handler.Requests.Count);
    }

    [Fact]
    public async Task GetJsonAsync_Offline_SendsNothing()
    {
        _world.Settings.Current.External.OfflineMode = true;

        var root = await _world.Http.GetJsonAsync(ExternalApi.Holidays, Target);

        Assert.Null(root);
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task GetJsonAsync_OfflineWhileWaitingToRetry_DoesNotRetry()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(HttpStatusCode.ServiceUnavailable));
        var http = new ExternalHttp(_world.Factory, _world.Settings, _world.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExternalHttp>.Instance)
        {
            Delay = (_, _) =>
            {
                _world.Settings.Current.External.OfflineMode = true;
                return Task.CompletedTask;
            },
        };

        Assert.Null(await http.GetJsonAsync(ExternalApi.Weather, Target));
        Assert.Single(_world.Handler.Requests);
    }

    [Fact]
    public async Task GetJsonAsync_PlainHttp_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _world.Http.GetJsonAsync(ExternalApi.LinkPreview, new Uri("http://example.test/")));
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task GetJsonAsync_CallerCancels_ThrowsOperationCanceled()
    {
        using var cancel = new CancellationTokenSource();
        _world.Handler.Respond = async (_, ct) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeExternalHandler.Json("{}");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _world.Http.GetJsonAsync(ExternalApi.Weather, Target, cancel.Token));
        Assert.Single(_world.Handler.Requests);
    }
}

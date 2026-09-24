using TaskDeck.Core.Abstractions;
using TaskDeck.Data.External;

namespace TaskDeck.Tests.Insights;

/// <summary>更新の確認（GitHub Releases）: リポジトリ名が null の間は通信しない。送るのはリポジトリ名だけ。1日1回。</summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private readonly ExternalWorld _world = new();

    public UpdateCheckerTests() =>
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json(
            """{"tag_name":"v0.2.0","name":"TaskDeck 0.2.0","draft":false,"prerelease":false,"html_url":"https://github.com/owner/taskdeck/releases/tag/v0.2.0"}"""));

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Repository_IsThePublicRepository() => Assert.Equal("takosasi-dev/taskdeck", UpdateChecker.Repository);

    [Fact]
    public async Task CheckAsync_NoRepository_SendsNothing()
    {
        var checker = _world.NewUpdateChecker(repository: null);

        var status = await checker.CheckAsync(force: true);

        Assert.Equal(UpdateState.NotConfigured, status.State);
        Assert.Null(checker.ReleasesPage);
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task CheckAsync_NewerRelease_ReportsAvailableAndRemembers()
    {
        var checker = _world.NewUpdateChecker("owner/taskdeck", currentVersion: "0.1.0.0");

        var status = await checker.CheckAsync(force: false);

        Assert.Equal("https://api.github.com/repos/owner/taskdeck/releases/latest", Assert.Single(_world.Handler.Uris).AbsoluteUri);
        Assert.Equal(UpdateState.Available, status.State);
        Assert.Equal("v0.2.0", status.LatestVersion);
        Assert.Equal(_world.Clock.UtcNow, status.CheckedAtUtc);
        Assert.Equal("v0.2.0", await _world.State.GetAsync(AppStateKeys.LatestKnownVersion));
        Assert.Equal("https://github.com/owner/taskdeck/releases/latest", checker.ReleasesPage);

        // 覚えた結果は通信せずに読める
        _world.Handler.Requests.Clear();
        Assert.Equal(UpdateState.Available, (await checker.GetStatusAsync()).State);
        Assert.Empty(_world.Handler.Requests);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.3.1")]
    public async Task CheckAsync_SameOrNewerVersion_ReportsUpToDate(string current)
    {
        var status = await _world.NewUpdateChecker("owner/taskdeck", current).CheckAsync(force: false);

        Assert.Equal(UpdateState.UpToDate, status.State);
    }

    [Fact]
    public async Task CheckAsync_WithinADay_SendsOnlyOnce()
    {
        var checker = _world.NewUpdateChecker("owner/taskdeck");
        await checker.CheckAsync(force: false);

        _world.Clock.Advance(TimeSpan.FromHours(23));
        await checker.CheckAsync(force: false);
        Assert.Single(_world.Handler.Requests);

        _world.Clock.Advance(TimeSpan.FromHours(1));
        await checker.CheckAsync(force: false);
        Assert.Equal(2, _world.Handler.Requests.Count);
    }

    [Fact]
    public async Task CheckAsync_DisabledOrOffline_SendsNothing()
    {
        var checker = _world.NewUpdateChecker("owner/taskdeck");

        _world.Settings.Current.External.UpdateCheckEnabled = false;
        Assert.Equal(UpdateState.Disabled, (await checker.CheckAsync(force: true)).State);

        _world.Settings.Current.External.UpdateCheckEnabled = true;
        _world.Settings.Current.External.OfflineMode = true;
        Assert.Equal(UpdateState.Offline, (await checker.CheckAsync(force: true)).State);

        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task CheckAsync_UnexpectedShape_StaysUnknown()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json("""{"message":"Not Found"}"""));

        var status = await _world.NewUpdateChecker("owner/taskdeck").CheckAsync(force: false);

        Assert.Equal(UpdateState.Unknown, status.State);
        Assert.Null(await _world.State.GetAsync(AppStateKeys.LastUpdateCheck));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2.0.1-beta.1", "2.0.1")]
    [InlineData("release-1", null)]
    [InlineData("", null)]
    public void ParseVersion_Tag_ReadsNumbers(string tag, string? expected) =>
        Assert.Equal(expected, UpdateChecker.ParseVersion(tag)?.ToString());
}

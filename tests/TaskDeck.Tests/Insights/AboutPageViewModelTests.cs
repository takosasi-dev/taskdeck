using TaskDeck.App;
using TaskDeck.App.Settings.Pages;
using TaskDeck.Data.External;

namespace TaskDeck.Tests.Insights;

/// <summary>「TaskDeck について」の更新の確認の欄（「最新です」「新しい版があります」）。</summary>
public sealed class AboutPageViewModelTests : IDisposable
{
    private static readonly AppPaths Paths = new(@"C:\TaskDeckTest", IsDevelopment: false);

    private readonly ExternalWorld _world = new();

    public AboutPageViewModelTests() =>
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json("""{"tag_name":"v0.2.0"}"""));

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData(UpdateState.NotConfigured, null, "公開先が決まると、ここで新しい版が出ていないか確かめられるようになります")]
    [InlineData(UpdateState.Disabled, null, "更新の確認はオフです（「外部サービス」で切り替えられます）")]
    [InlineData(UpdateState.Offline, null, "オフラインモードのため確認していません")]
    [InlineData(UpdateState.Unknown, null, "まだ確認していません")]
    [InlineData(UpdateState.UpToDate, "v0.1.0", "最新です（バージョン 0.1.0）")]
    [InlineData(UpdateState.Available, "v0.2.0", "新しい版があります: v0.2.0（今はバージョン 0.1.0）")]
    public void Describe_State_ReturnsSentence(UpdateState state, string? latest, string expected) =>
        Assert.Equal(expected, AboutPageViewModel.Describe(new UpdateStatus(state, latest, null), "0.1.0"));

    [Fact]
    public async Task LoadAsync_RepositoryNotDecided_CannotCheck()
    {
        var viewModel = new AboutPageViewModel(Paths, _world.NewUpdateChecker(repository: null), _world.Clock);

        await viewModel.LoadAsync();

        Assert.StartsWith("公開先が決まると", viewModel.UpdateStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.CheckNowCommand.CanExecute(null));
        Assert.False(viewModel.HasUpdate);
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task CheckNow_NewerRelease_ShowsItWithTime()
    {
        var viewModel = new AboutPageViewModel(Paths, _world.NewUpdateChecker("owner/taskdeck", "0.1.0"), _world.Clock);
        await viewModel.LoadAsync();
        Assert.Equal("まだ確認していません", viewModel.UpdateStatusText);
        Assert.True(viewModel.CheckNowCommand.CanExecute(null));

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasUpdate);
        Assert.Equal("新しい版があります: v0.2.0（今はバージョン 0.1.0）", viewModel.UpdateStatusText);
        Assert.Equal("最後に確認: 2026/09/23 10:00", viewModel.UpdateCheckedText);
    }

    [Fact]
    public async Task CheckNow_Failure_SaysItCouldNotCheck()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Status(System.Net.HttpStatusCode.BadGateway));
        var viewModel = new AboutPageViewModel(Paths, _world.NewUpdateChecker("owner/taskdeck"), _world.Clock);
        await viewModel.LoadAsync();

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal("確認できませんでした。時間をおいて試してください", viewModel.UpdateStatusText);
        Assert.False(viewModel.HasUpdate);
    }

    [Fact]
    public void ValuesOnlyConstructor_HidesUpdateCard()
    {
        Assert.False(new AboutPageViewModel(Paths).ShowsUpdates);
        Assert.Equal(4, new AboutPageViewModel(Paths).ExternalServices.Count);
    }
}

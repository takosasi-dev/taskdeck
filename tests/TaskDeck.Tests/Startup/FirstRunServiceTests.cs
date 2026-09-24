using TaskDeck.App;
using TaskDeck.App.Views.Startup;
using TaskDeck.Core.Abstractions;
using TaskDeck.Tests.Residency;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Startup;

/// <summary>初回起動の3画面を出すかどうか（本番の構成。開発用フォルダは環境変数で決まるのでここでは見ない）。</summary>
public class FirstRunServiceTests
{
    private readonly ResidencyAppState _state = new();
    private readonly FirstRunService _service;

    public FirstRunServiceTests() =>
        _service = new FirstRunService(_state, FixedClock.AtLocal(2026, 9, 22, 10, 0), new AppPaths(@"C:\TaskDeckTests", IsDevelopment: false));

    [Fact]
    public async Task ShouldShowAsync_ProductionWithoutRecord_ReturnsTrue()
    {
        Assert.True(await _service.ShouldShowAsync());
        Assert.Null(_service.DevStartStep);
    }

    [Fact]
    public async Task ShouldShowAsync_ProductionAfterMarkCompleted_ReturnsFalse()
    {
        await _service.MarkCompletedAsync();

        Assert.False(await _service.ShouldShowAsync());
        Assert.Equal("2026-09-22", _state.Values[AppStateKeys.FirstRunCompleted]);
    }
}

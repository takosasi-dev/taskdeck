using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Scratch;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Scratch;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Scratch;

/// <summary>メイン画面の中央の使い捨てリスト（小窓に出している間の表示）。窓は出さない。</summary>
public class ScratchPaneViewModelTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private readonly ScratchPresenter _presenter;
    private readonly ScratchPaneViewModel _pane;

    public ScratchPaneViewModelTests()
    {
        var state = new MemoryAppState();
        var shell = new ShellService(Substitute.For<IServiceProvider>(), Clock, NullLogger<ShellService>.Instance);
        _presenter = new ScratchPresenter(new ScratchStore(state, Clock), state, Substitute.For<ITemplateRepository>(), shell,
            Substitute.For<IServiceProvider>(), NullLogger<ScratchPresenter>.Instance);
        _pane = new ScratchPaneViewModel(_presenter);
    }

    [Fact]
    public void Show_メイン画面に出す_次の新しいリストもメイン画面で開く()
    {
        var list = _presenter.Store.Create("買い物");

        _pane.Show(list.Id);

        Assert.Equal(list.Id, _pane.List?.Id);
        Assert.False(_pane.IsDetached);
        Assert.Equal(ScratchPlace.Main, _presenter.WindowState.LastPlace);
    }

    [Fact]
    public void Show_小窓に出しているリスト_小窓で表示中にして場所は覚え直さない()
    {
        var list = _presenter.Store.Create("買い物");
        _presenter.SetWindowList(list.Id);

        _pane.Show(list.Id);

        Assert.True(_pane.IsDetached);
        Assert.Equal(ScratchPlace.Window, _presenter.WindowState.LastPlace);
    }

    [Fact]
    public void 小窓で捨てて窓が閉じた_消えたリストの中身は出し直さない()
    {
        var list = _presenter.Store.Create("買い物");
        _presenter.SetWindowList(list.Id);
        _pane.Show(list.Id);
        var shown = _pane.List;

        _presenter.Store.Discard(list.Id);
        _presenter.SetWindowList(null);

        Assert.True(_pane.IsDetached);
        Assert.Same(shown, _pane.List);
    }

    [Fact]
    public void Show_見つからないリスト_空にする()
    {
        _pane.Show(Guid.CreateVersion7());

        Assert.Null(_pane.List);
        Assert.Equal(ScratchPlace.Window, _presenter.WindowState.LastPlace);
    }
}

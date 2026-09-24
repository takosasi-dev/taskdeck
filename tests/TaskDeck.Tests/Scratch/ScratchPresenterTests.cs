using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Scratch;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Scratch;
using TaskDeck.Core.Services;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Scratch;

/// <summary>どこで開くかを決めるサービス（最後に使った場所・小窓の設定の保存・テンプレートから作る）。窓は出さない。</summary>
public class ScratchPresenterTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private readonly MemoryAppState _state = new();
    private readonly ShellService _shell = new(Substitute.For<IServiceProvider>(), Clock, NullLogger<ShellService>.Instance);

    private ScratchPresenter NewPresenter(ITemplateRepository? templates = null, ScratchStore? store = null) =>
        new(store ?? new ScratchStore(_state, Clock), _state, templates ?? Substitute.For<ITemplateRepository>(), _shell,
            Substitute.For<IServiceProvider>(), NullLogger<ScratchPresenter>.Instance);

    [Fact]
    public async Task LoadAsync_覚えた場所が無い_最初は小窓()
    {
        var presenter = NewPresenter();

        await presenter.LoadAsync();

        Assert.Equal(ScratchPlace.Window, presenter.WindowState.LastPlace);
        Assert.False(presenter.WindowState.Pinned);
    }

    [Fact]
    public async Task NoteShownIn_メイン画面に出した_覚えて次の起動でもメイン画面()
    {
        var presenter = NewPresenter();

        presenter.NoteShownIn(ScratchPlace.Main);
        await presenter.FlushAsync();
        var restarted = NewPresenter();
        await restarted.LoadAsync();

        Assert.Equal(ScratchPlace.Main, restarted.WindowState.LastPlace);
        Assert.Contains("\"lastPlace\":\"Main\"", _state.Values[AppStateKeys.ScratchWindow], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoteShownIn_同じ場所_書かない()
    {
        var presenter = NewPresenter();

        presenter.NoteShownIn(ScratchPlace.Window);
        await presenter.FlushAsync();

        Assert.False(_state.Values.ContainsKey(AppStateKeys.ScratchWindow));
    }

    [Fact]
    public async Task SaveWindowState_位置と大きさとピン_次の起動で戻る()
    {
        var presenter = NewPresenter();
        presenter.WindowState.Left = 1200;
        presenter.WindowState.Top = 80;
        presenter.WindowState.Width = 360;
        presenter.WindowState.Height = 520;
        presenter.WindowState.Pinned = true;

        presenter.SaveWindowState();
        await presenter.FlushAsync();
        var restarted = NewPresenter();
        await restarted.LoadAsync();

        var state = restarted.WindowState;
        Assert.Equal((1200d, 80d, 360d, 520d, true), (state.Left, state.Top, state.Width, state.Height, state.Pinned));
    }

    [Fact]
    public async Task LoadAsync_壊れた設定_既定の値で始める()
    {
        _state.Values[AppStateKeys.ScratchWindow] = "{ broken";
        var presenter = NewPresenter();

        await presenter.LoadAsync();

        Assert.Equal(ScratchPlace.Window, presenter.WindowState.LastPlace);
    }

    [Fact]
    public async Task CreateFromTemplateAsync_買い物リスト_タスクを1件も作らず題名と階層だけのリストにする()
    {
        using var db = new TestDatabase(Clock);
        var hub = new DataChangeHub();
        var settings = new FakeSettingsStore();
        var templates = new TemplateRepository(db, Clock, settings, hub);
        var published = DataChangeKind.None;
        await templates.SeedDefaultsAsync();
        hub.Changed += (_, e) => published |= e.Kinds;
        var shopping = (await templates.GetAllAsync()).Single(t => t.Template.Name == "買い物リスト").Template;
        var store = new ScratchStore(new AppStateRepository(db), Clock);
        var presenter = NewPresenter(templates, store);

        var list = await presenter.CreateFromTemplateAsync(shopping.Id);
        await presenter.FlushAsync();

        Assert.NotNull(list);
        Assert.Equal("買い物リスト", list.Name);
        Assert.Equal(["食料品", "牛乳", "卵", "パン", "日用品", "ティッシュペーパー"], list.Items.Select(i => i.Text));
        Assert.Equal([0, 1, 1, 1, 0, 1], list.Items.Select(i => i.Depth));
        Assert.All(list.Items, i => Assert.False(i.IsChecked));
        using (var context = db.CreateDbContext())
        {
            Assert.Equal(0, context.Tasks.Count());
        }
        Assert.Equal(DataChangeKind.None, published);   // タスクの一覧・件数・トレイは読み直さない
        Assert.Equal(0, (await templates.GetAllAsync()).Single(t => t.Template.Id == shopping.Id).Template.UseCount);
        var reloaded = new ScratchStore(new AppStateRepository(db), Clock);
        await reloaded.LoadAsync();
        Assert.Equal(list.Id, Assert.Single(reloaded.Lists).Id);
    }

    [Fact]
    public async Task CreateFromTemplateAsync_テンプレートが消えていた_作らずnull()
    {
        var presenter = NewPresenter();

        var list = await presenter.CreateFromTemplateAsync(Guid.CreateVersion7());

        Assert.Null(list);
        Assert.Empty(presenter.Store.Lists);
    }

    [Fact]
    public async Task CreateNew_空のリスト_新しいリストという名前で作り保存し切れる()
    {
        var presenter = NewPresenter();

        var list = presenter.CreateNew();
        await presenter.FlushAsync();

        Assert.Equal(ScratchStore.DefaultName, list.Name);
        Assert.Empty(list.Items);
        Assert.Contains(list.Id.ToString(), _state.Values[AppStateKeys.ScratchLists], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetWindowList_小窓の中身が変わった_知らせる()
    {
        var presenter = NewPresenter();
        var id = Guid.CreateVersion7();
        var raised = 0;
        presenter.WindowListChanged += (_, _) => raised++;

        presenter.SetWindowList(id);
        presenter.SetWindowList(id);
        presenter.SetWindowList(null);

        Assert.Equal(2, raised);
        Assert.Null(presenter.WindowListId);
    }
}

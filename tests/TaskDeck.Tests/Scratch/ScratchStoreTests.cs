using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Scratch;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Scratch;

/// <summary>使い捨てリストの保存役（JSON の往復・名前・捨てる・保存の条件）。</summary>
public class ScratchStoreTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private readonly MemoryAppState _state = new();

    private ScratchStore NewStore() => new(_state, Clock);

    [Fact]
    public async Task SaveAsync_保存して別のストアで読む_名前と項目と日時が戻る()
    {
        var store = NewStore();
        var list = store.Create("買い物",
        [
            new ScratchItem { Text = "牛乳", IsChecked = true },
            new ScratchItem { Text = "パン" },
            new ScratchItem { Text = "食パン 6枚切り", Depth = 1 },
        ]);

        await store.SaveAsync();
        var reloaded = NewStore();
        await reloaded.LoadAsync();

        var copy = Assert.Single(reloaded.Lists);
        Assert.Equal(list.Id, copy.Id);
        Assert.Equal("買い物", copy.Name);
        Assert.Equal(Clock.UtcNow, copy.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, copy.CreatedAt.Kind);
        Assert.Equal(["牛乳", "パン", "食パン 6枚切り"], copy.Items.Select(i => i.Text));
        Assert.Equal([true, false, false], copy.Items.Select(i => i.IsChecked));
        Assert.Equal([0, 0, 1], copy.Items.Select(i => i.Depth));
        Assert.Equal(list.Items.Select(i => i.Id), copy.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task SaveAsync_日本語はそのまま書く_計算用の値は書かない()
    {
        var store = NewStore();
        store.Create("買い物", [new ScratchItem { Text = "牛乳" }]);

        await store.SaveAsync();

        var json = _state.Values[AppStateKeys.ScratchLists];
        Assert.Contains("牛乳", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isBlank", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_変更が無い_書かない()
    {
        var store = NewStore();
        store.Create("買い物");
        await store.SaveAsync();

        await store.SaveAsync();
        await store.FlushAsync();

        Assert.Equal(1, _state.Writes);
        Assert.False(store.IsDirty);
    }

    [Fact]
    public async Task SaveAsync_本物のDBに保存して読み直す_残っている()
    {
        using var db = new TestDatabase(Clock);
        var appState = new AppStateRepository(db);
        var store = new ScratchStore(appState, Clock);
        store.Create("旅行の準備", [new ScratchItem { Text = "充電器" }]);

        await store.FlushAsync();
        var reloaded = new ScratchStore(appState, Clock);
        await reloaded.LoadAsync();

        Assert.Equal("旅行の準備", Assert.Single(reloaded.Lists).Name);
    }

    [Fact]
    public async Task LoadAsync_壊れた内容_空で始めて問題と元の文字を残す()
    {
        _state.Values[AppStateKeys.ScratchLists] = "[{\"name\": ";
        var store = NewStore();

        await store.LoadAsync();

        Assert.Empty(store.Lists);
        Assert.NotNull(store.LoadProblem);
        Assert.Equal("[{\"name\": ", store.BrokenJson);
    }

    [Fact]
    public async Task LoadAsync_手で直された内容_欠けた値を埋めて深さを整える()
    {
        _state.Values[AppStateKeys.ScratchLists] =
            "[{\"id\":\"0199a000-0000-7000-8000-000000000001\",\"name\":\"  \",\"items\":[{\"text\":null,\"depth\":5},null,{\"text\":\"子\",\"depth\":3}]},null]";
        var store = NewStore();

        await store.LoadAsync();

        var list = Assert.Single(store.Lists);
        Assert.Equal(ScratchStore.DefaultName, list.Name);
        Assert.Equal(["", "子"], list.Items.Select(i => i.Text));
        Assert.Equal([0, 1], list.Items.Select(i => i.Depth));
    }

    [Fact]
    public void Create_名前が空や重なる_新しいリストと番号を付ける()
    {
        var store = NewStore();

        var first = store.Create(null);
        var second = store.Create("  ");
        var shopping = store.Create("買い物");
        var shopping2 = store.Create("買い物");

        Assert.Equal(["新しいリスト", "新しいリスト 2", "買い物", "買い物 2"], new[] { first, second, shopping, shopping2 }.Select(l => l.Name));
    }

    [Fact]
    public void Create_深すぎる項目_詰めて作り増えたことを知らせる()
    {
        var store = NewStore();
        var changes = new List<ScratchChangedEventArgs>();
        store.Changed += (_, e) => changes.Add(e);

        var list = store.Create("買い物", [new ScratchItem { Text = "卵", Depth = 2 }]);

        Assert.Equal(0, list.Items[0].Depth);
        Assert.True(store.IsDirty);
        Assert.Equal((ScratchChange.Created, list.Id), (changes.Single().Change, changes.Single().ListId));
    }

    [Fact]
    public void Rename_空や同じ名前_変えない()
    {
        var store = NewStore();
        var list = store.Create("買い物");
        var changes = 0;
        store.Changed += (_, _) => changes++;

        Assert.False(store.Rename(list.Id, "   "));
        Assert.False(store.Rename(list.Id, "買い物"));
        Assert.True(store.Rename(list.Id, "　週末の買い物 "));

        Assert.Equal("週末の買い物", list.Name);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Discard_捨てる_一覧から消えて保存にも残らない()
    {
        var store = NewStore();
        var list = store.Create("買い物");
        var keep = store.Create("旅行の準備");
        ScratchChangedEventArgs? change = null;
        store.Changed += (_, e) => change = e;

        Assert.True(store.Discard(list.Id));
        await store.SaveAsync();
        var reloaded = NewStore();
        await reloaded.LoadAsync();

        Assert.Equal(ScratchChange.Discarded, change?.Change);
        Assert.Equal([keep.Id], store.Lists.Select(l => l.Id));
        Assert.Equal([keep.Id], reloaded.Lists.Select(l => l.Id));
        Assert.False(store.Discard(list.Id));
    }

    [Fact]
    public async Task SaveAsync_書き込みに失敗_変更ありのまま例外を上げる()
    {
        var failing = Substitute.For<IAppStateRepository>();
        failing.SetAsync(default!, default).ThrowsAsyncForAnyArgs(new IOException("disk"));
        var store = new ScratchStore(failing, Clock);
        store.Create("買い物");

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync());

        Assert.True(store.IsDirty);
    }
}

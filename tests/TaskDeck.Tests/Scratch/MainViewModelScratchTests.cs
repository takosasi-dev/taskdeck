using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Scratch;
using TaskDeck.Tests.App.ViewModels;

namespace TaskDeck.Tests.Scratch;

/// <summary>メイン画面への組み込み（中央に出す・起動時ビュー・捨てたら戻る・サイドバー・検索・Ctrl+N・タスクに触らない）。</summary>
public class MainViewModelScratchTests
{
    private readonly ViewModelFixture _f = new();

    private ScratchList Shopping() =>
        _f.ScratchStore.Create("買い物", [new ScratchItem { Text = "牛乳", IsChecked = true }, new ScratchItem { Text = "卵" }]);

    private async Task<MainViewModel> StartAsync()
    {
        var main = _f.CreateMain();
        await main.InitializeAsync();
        return main;
    }

    [Fact]
    public async Task SelectViewAsync_使い捨てリスト_中央に出して一覧の条件は空にし詳細ペインを出さない()
    {
        var task = _f.NewTask("会議資料まとめる", new DateTime(2026, 9, 22));
        _f.AddRow(task);
        var list = Shopping();
        var main = await StartAsync();
        main.List.SelectedItem = ViewModelFixture.TaskRows(main.List).Single();
        Assert.True(main.IsDetailVisible);

        await main.SelectViewAsync(ViewKey.ForScratch(list.Id));

        Assert.True(main.IsScratchMode);
        Assert.False(main.IsListMode);
        Assert.Null(_f.Shell.CurrentListQuery);
        Assert.False(main.IsDetailVisible);
        Assert.Equal(list.Id, main.Scratch.List?.Id);
        Assert.False(main.Scratch.IsDetached);
        Assert.Equal("買い物", main.Title);
        Assert.False(main.UndoCommand.CanExecute(null));   // Ctrl+Z で見えないタスクを戻さない
    }

    [Fact]
    public async Task SelectViewAsync_一覧に戻る_使い捨ての表示を片づける()
    {
        var list = Shopping();
        var main = await StartAsync();
        await main.SelectViewAsync(ViewKey.ForScratch(list.Id));

        await main.SelectViewAsync(ViewKey.All);

        Assert.False(main.IsScratchMode);
        Assert.Null(main.Scratch.List);
        Assert.NotNull(_f.Shell.CurrentListQuery);
        Assert.True(main.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task InitializeAsync_起動時ビューのリストが捨てられていた_今日を開く()
    {
        _f.Settings.Current.General.StartView = ViewKey.ForScratch(Guid.CreateVersion7()).ToString();

        var main = await StartAsync();

        Assert.Equal(ViewKey.Today, main.CurrentView);
        Assert.False(main.IsScratchMode);
    }

    [Fact]
    public async Task InitializeAsync_起動時ビューのリストが残っている_そのリストを開く()
    {
        var list = Shopping();
        _f.Settings.Current.General.StartView = ViewKey.ForScratch(list.Id).ToString();

        var main = await StartAsync();

        Assert.Equal(ViewKey.ForScratch(list.Id), main.CurrentView);
        Assert.True(main.IsScratchMode);
    }

    [Fact]
    public async Task NavigateAsync_起動時の読み込みの前に来た_読み込みの後にそのリストを開く()
    {
        var list = Shopping();
        _f.Settings.Current.General.StartView = ViewKey.Upcoming.ToString();
        var main = _f.CreateMain();

        // メイン画面を初めて出すのと同時に「メイン画面に戻す」が来た（ShellService.NavigateTo）
        await main.NavigateAsync(ViewKey.ForScratch(list.Id));
        await main.InitializeAsync();

        Assert.Equal(ViewKey.ForScratch(list.Id), main.CurrentView);
        Assert.Equal(list.Id, main.Scratch.List?.Id);
    }

    [Fact]
    public async Task 出しているリストを捨てる_最後に出していた一覧へ戻りサイドバーからも消える()
    {
        var list = Shopping();
        var main = await StartAsync();
        await main.SelectViewAsync(ViewKey.All);
        await main.SelectViewAsync(ViewKey.ForScratch(list.Id));
        main.Scratch.List!.ConfirmDiscard = _ => true;

        main.Scratch.List.DiscardCommand.Execute(null);

        Assert.True(await ViewModelFixture.WaitUntilAsync(() => main.CurrentView == ViewKey.All));
        Assert.True(await ViewModelFixture.WaitUntilAsync(() => main.Sidebar.Items.All(i => !i.IsScratch)));
        Assert.Null(main.Scratch.List);
        Assert.False(main.Sidebar.Contains(ViewKey.ForScratch(list.Id)));
    }

    [Fact]
    public async Task サイドバー_使い捨ての見出しの下にリストを並べ_見出しのプラスで新しいリストを頼む()
    {
        var list = Shopping();
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();
        var commands = new List<string>();
        sidebar.CommandInvoked += (_, id) => commands.Add(id);

        var labels = sidebar.Items.Select(i => i.IsSeparator ? "-" : i.Label).ToList();
        var section = sidebar.Items.Single(i => i.HasSectionAction);
        section.InvokeCommand!.Execute(section);

        Assert.Equal(["今日", "予定", "すべて", "-", "使い捨て", "買い物", "-", "プロジェクト"], labels.Take(8));
        Assert.True(sidebar.Contains(ViewKey.ForScratch(list.Id)));
        Assert.False(sidebar.Contains(ViewKey.ForScratch(Guid.CreateVersion7())));
        Assert.Equal("新しい使い捨てリスト", section.ActionName);
        Assert.Equal([SidebarViewModel.NewScratchCommandId], commands);
        Assert.False(sidebar.Items.Single(i => i.IsScratch).ShowCount);
    }

    [Fact]
    public async Task サイドバー_リストが無い_見出しとプラスだけ出す()
    {
        var sidebar = _f.CreateSidebar();

        await sidebar.ReloadAsync();

        Assert.Single(sidebar.Items, i => i.HasSectionAction);
        Assert.DoesNotContain(sidebar.Items, i => i.IsScratch);
    }

    [Fact]
    public async Task リストが増えて名前が変わる_サイドバーを作り直す()
    {
        var main = await StartAsync();

        var list = Shopping();
        _f.ScratchStore.Rename(list.Id, "週末の買い物");

        Assert.True(await ViewModelFixture.WaitUntilAsync(() => main.Sidebar.Items.Any(i => i.IsScratch && i.Label == "週末の買い物")));
    }

    [Fact]
    public async Task 検索欄に打つ_使い捨てから一覧に戻って探す()
    {
        var list = Shopping();
        var main = await StartAsync();
        await main.SelectViewAsync(ViewKey.ForScratch(list.Id));

        main.UpdateSearchText("牛乳", isComposing: false);
        await main.ApplySearchAsync();

        Assert.True(main.IsListMode);
        Assert.False(main.IsScratchMode);
        Assert.Equal("牛乳", main.List.SearchText);
        Assert.NotNull(_f.Shell.CurrentListQuery);
    }

    [Fact]
    public async Task CtrlN_使い捨てを出している_リストの項目の入力へ()
    {
        var list = Shopping();
        var main = await StartAsync();
        await main.SelectViewAsync(ViewKey.ForScratch(list.Id));
        var scratchAdds = 0;
        var taskAdds = 0;
        main.ScratchAddRequested += (_, _) => scratchAdds++;
        main.AddTaskRequested += (_, _) => taskAdds++;

        await main.BeginAddTaskCommand.ExecuteAsync(null);

        Assert.Equal((1, 0), (scratchAdds, taskAdds));
        Assert.True(main.IsScratchMode);
    }

    [Fact]
    public async Task 小窓に出しているリストを選ぶ_小窓で表示中にし閉じたら中身を出し直す()
    {
        var list = Shopping();
        var main = await StartAsync();
        _f.ScratchPresenter.SetWindowList(list.Id);

        await main.SelectViewAsync(ViewKey.ForScratch(list.Id));
        var before = main.Scratch.List;
        Assert.True(main.Scratch.IsDetached);

        // 小窓で書き換えてから閉じた
        list.Items.Add(new ScratchItem { Text = "パン" });
        _f.ScratchPresenter.SetWindowList(null);

        Assert.False(main.Scratch.IsDetached);
        Assert.NotSame(before, main.Scratch.List);
        Assert.Equal(["牛乳", "卵", "パン"], main.Scratch.List!.Items.Select(i => i.Text));
    }

    [Fact]
    public async Task 小窓に出している間のCtrlN_タスクの入力行へ()
    {
        var list = Shopping();
        var main = await StartAsync();
        _f.ScratchPresenter.SetWindowList(list.Id);
        await main.SelectViewAsync(ViewKey.ForScratch(list.Id));
        var taskAdds = 0;
        main.AddTaskRequested += (_, _) => taskAdds++;

        await main.BeginAddTaskCommand.ExecuteAsync(null);

        Assert.Equal(1, taskAdds);
        Assert.True(main.IsListMode);
    }

    [Fact]
    public async Task 使い捨ての操作_タスクには一切書かずサイドバーの件数も変わらない()
    {
        _f.Counts = ViewCounts.Empty with { Today = 3, Upcoming = 5, AllOpen = 12 };
        var list = Shopping();
        var main = await StartAsync();
        await main.SelectViewAsync(ViewKey.ForScratch(list.Id));
        var vm = main.Scratch.List!;

        vm.AddText = "パン";
        vm.AddFromBoxCommand.Execute(null);
        vm.Indent(vm.Items[2]);
        vm.Toggle(vm.Items[1]);
        vm.NameText = "週末の買い物";
        vm.ConfirmDiscard = _ => true;
        vm.DiscardCommand.Execute(null);
        Assert.True(await ViewModelFixture.WaitUntilAsync(() => main.Sidebar.Items.All(i => !i.IsScratch)));

        // 読むだけ（一覧・件数・1件）のほかは呼ばれていない
        var others = _f.Tasks.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Where(n => n is not (nameof(ITaskRepository.QueryAsync) or nameof(ITaskRepository.GetViewCountsAsync) or nameof(ITaskRepository.GetAsync)))
            .ToList();
        Assert.Empty(others);
        Assert.Equal([3, 5, 12], main.Sidebar.Items.Take(3).Select(i => i.Count));
    }
}

using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>メイン画面の中心（Esc の段階・ShellService へ渡す値・検索・取り消しトースト）。</summary>
public class MainViewModelTests
{
    private readonly ViewModelFixture _f = new();

    private static DateTime Local(int month, int day) => new(2026, month, day, 0, 0, 0);

    private async Task<MainViewModel> StartAsync()
    {
        var main = _f.CreateMain();
        await main.InitializeAsync();
        return main;
    }

    [Fact]
    public async Task HandleEscapeAsync_検索と詳細ペインがある_検索クリア詳細を閉じるウィンドウの順に戻る()
    {
        _f.AddRow(_f.NewTask("会議資料まとめる", Local(9, 22)));
        var main = await StartAsync();
        main.List.SelectedItem = ViewModelFixture.TaskRows(main.List).Single();
        main.UpdateSearchText("会議", isComposing: false);
        await main.ApplySearchAsync();

        Assert.Equal(EscapeAction.ClearedSearch, await main.HandleEscapeAsync());
        Assert.Equal("", main.SearchText);
        Assert.Null(main.List.SearchText);
        Assert.True(main.IsDetailVisible);

        Assert.Equal(EscapeAction.ClosedDetailPane, await main.HandleEscapeAsync());
        Assert.False(main.IsDetailVisible);

        Assert.Equal(EscapeAction.HidWindow, await main.HandleEscapeAsync());
    }

    [Fact]
    public async Task HandleEscapeAsync_変換中の文字だけ入っている_まず検索欄を空にする()
    {
        var main = await StartAsync();
        main.UpdateSearchText("かいぎ", isComposing: true);

        Assert.Equal(EscapeAction.ClearedSearch, await main.HandleEscapeAsync());
        Assert.Equal("", main.SearchText);
    }

    [Fact]
    public async Task UpdateSearchText_変換中_一覧を絞らない()
    {
        var main = await StartAsync();
        var before = _f.Queries.Count;

        main.UpdateSearchText("かいぎ", isComposing: true);
        await Task.Delay(MainViewModel.SearchDebounceMs + 150);

        Assert.Equal(before, _f.Queries.Count);
        Assert.Null(main.List.SearchText);
    }

    [Fact]
    public async Task 一覧で行を選ぶ_ShellServiceの選択中タスクと詳細ペインに入る()
    {
        var task = _f.NewTask("会議資料まとめる", Local(9, 22));
        _f.AddRow(task);
        var main = await StartAsync();

        main.List.SelectedItem = ViewModelFixture.TaskRows(main.List).Single();

        Assert.Equal([task.Id], _f.Shell.SelectedTaskIds);
        Assert.Equal(task.Id, main.SelectedTaskId);
        Assert.True(main.IsDetailPaneOpen);
    }

    [Fact]
    public async Task 行が読み直しで外れる_ShellServiceは空にし詳細ペインは残す()
    {
        var task = _f.NewTask("会議資料まとめる", Local(9, 22));
        var row = _f.AddRow(task);
        var main = await StartAsync();
        main.List.SelectedItem = ViewModelFixture.TaskRows(main.List).Single();

        _f.Rows.Remove(row);
        await main.List.ReloadAsync();

        Assert.Empty(_f.Shell.SelectedTaskIds);
        Assert.Equal(task.Id, main.SelectedTaskId);
    }

    [Fact]
    public async Task SelectViewAsync_一覧のビュー_ShellServiceに一覧の条件を入れる()
    {
        var main = await StartAsync();

        await main.SelectViewAsync(ViewKey.Upcoming);

        Assert.NotNull(_f.Shell.CurrentListQuery);
        Assert.Equal(DueFilter.Next7Days, _f.Shell.CurrentListQuery.Due);
        Assert.Equal("予定", main.Title);
        Assert.Equal("予定", main.List.Title);
    }

    [Fact]
    public async Task SelectViewAsync_カレンダーと振り返り_ShellServiceの条件を空にする()
    {
        var main = await StartAsync();

        await main.SelectViewAsync(ViewKey.Calendar);
        Assert.Null(_f.Shell.CurrentListQuery);
        Assert.True(main.IsCalendarMode);
        Assert.False(main.IsListMode);

        await main.SelectViewAsync(ViewKey.Stats);
        Assert.Null(_f.Shell.CurrentListQuery);

        await main.ShowListCommand.ExecuteAsync(null);
        Assert.NotNull(_f.Shell.CurrentListQuery);
        Assert.True(main.IsListMode);
    }

    [Fact]
    public async Task ApplySearchAsync_検索語_ShellServiceの条件にも検索語と並び順が入る()
    {
        var main = await StartAsync();

        main.UpdateSearchText("資料", isComposing: false);
        await main.ApplySearchAsync();

        Assert.Equal("資料", _f.Shell.CurrentListQuery?.SearchText);
        Assert.Equal(TaskSortKey.Due, _f.Shell.CurrentListQuery?.SortKey);
    }

    [Fact]
    public async Task 空状態のすべてのタスクから探す_全状態で探し直し見出しを変える()
    {
        var main = await StartAsync();
        main.UpdateSearchText("棚卸し", isComposing: false);
        await main.ApplySearchAsync();

        await main.List.InvokeEmptyActionCommand.ExecuteAsync(null);

        Assert.True(await ViewModelFixture.WaitUntilAsync(() => _f.Shell.CurrentListQuery?.Status == TaskStatusFilter.All));
        Assert.True(main.List.SearchAllTasks);
        Assert.Equal(MainViewModel.AllTasksSearchTitle, main.List.Title);

        await main.ClearSearchAsync();
        Assert.Equal("今日", main.List.Title);
        Assert.False(main.List.SearchAllTasks);
    }

    [Fact]
    public async Task 取り消しトーストの依頼_元に戻すつきで出す()
    {
        var main = await StartAsync();
        var task = _f.NewTask("会議資料まとめる");

        _f.Undo.Record(ViewModelFixture.Changed(task), "「会議資料まとめる」を削除しました", UndoKind.Delete, showToast: true);

        Assert.True(main.Toast.IsOpen);
        Assert.Equal("「会議資料まとめる」を削除しました", main.Toast.Message);
        Assert.Equal("元に戻す", main.Toast.ActionLabel);
        Assert.Equal("IconTrash", main.Toast.IconKey);
    }

    [Fact]
    public async Task 取り消しトーストの後に別の操作を積む_押し違いを防ぐためトーストを閉じる()
    {
        var main = await StartAsync();
        _f.Undo.Record(ViewModelFixture.Changed(_f.NewTask("A")), "「A」を削除しました", UndoKind.Delete, showToast: true);

        _f.Undo.Record(ViewModelFixture.Changed(_f.NewTask("B")), "優先度を「高」にしました", UndoKind.Other, showToast: false);

        Assert.False(main.Toast.IsOpen);
    }

    [Fact]
    public async Task UserNotice_元に戻すなしのトーストを出す()
    {
        var main = await StartAsync();

        _f.Shell.NotifyUser("問題が発生しました");

        Assert.True(main.Toast.IsOpen);
        Assert.False(main.Toast.HasAction);
        Assert.Equal("問題が発生しました", main.Toast.Message);
    }

    [Fact]
    public async Task UndoCommand_直前の変更を戻してトーストを閉じる()
    {
        var main = await StartAsync();
        var result = ViewModelFixture.Changed(_f.NewTask("会議資料まとめる"));
        _f.Undo.Record(result, "「会議資料まとめる」を削除しました", UndoKind.Delete, showToast: true);

        await main.UndoCommand.ExecuteAsync(null);

        await _f.Tasks.Received(1).ApplyUndoAsync(result.Changes, Arg.Any<CancellationToken>());
        Assert.False(main.Toast.IsOpen);
        Assert.Equal(0, _f.Stack.Count);
    }

    [Fact]
    public async Task InitializeAsync_起動時ビューのプロジェクトが無い_今日を開く()
    {
        _f.Settings.Current.General.StartView = ViewKey.ForProject(Guid.CreateVersion7()).ToString();

        var main = await StartAsync();

        Assert.Equal(ViewKey.Today, main.CurrentView);
    }

    [Fact]
    public async Task InitializeAsync_起動時ビューがある_そのビューを開く()
    {
        var project = new Project { Name = "業務改善" };
        _f.ProjectList.Add(project);
        _f.Settings.Current.General.StartView = ViewKey.ForProject(project.Id).ToString();

        var main = await StartAsync();

        Assert.Equal(ViewKey.ForProject(project.Id), main.CurrentView);
        Assert.Equal("業務改善", main.Title);
    }

    [Fact]
    public async Task ToggleSidebarCommand_表示を切り替えて設定に残す()
    {
        var main = await StartAsync();

        main.ToggleSidebarCommand.Execute(null);

        Assert.False(main.IsSidebarVisible);
        Assert.False(_f.Settings.Current.Window.SidebarVisible);
    }

    [Fact]
    public async Task ToggleDetailPaneCommand_選んだタスクが無い_開かない()
    {
        var main = await StartAsync();

        main.ToggleDetailPaneCommand.Execute(null);

        Assert.False(main.IsDetailVisible);
    }

    [Fact]
    public async Task 詳細ペイン自動表示がオフ_選んでも開かずCtrlIで開く()
    {
        _f.Settings.Current.Appearance.AutoShowDetailPane = false;
        _f.AddRow(_f.NewTask("会議資料まとめる", Local(9, 22)));
        var main = await StartAsync();

        main.List.SelectedItem = ViewModelFixture.TaskRows(main.List).Single();
        Assert.False(main.IsDetailVisible);

        main.ToggleDetailPaneCommand.Execute(null);
        Assert.True(main.IsDetailVisible);
    }

    [Fact]
    public async Task 詳細ペインが狭い画面_自動で隠す()
    {
        _f.AddRow(_f.NewTask("会議資料まとめる", Local(9, 22)));
        var main = await StartAsync();
        main.List.SelectedItem = ViewModelFixture.TaskRows(main.List).Single();

        main.IsNarrow = true;

        Assert.False(main.IsDetailVisible);
        Assert.Equal(EscapeAction.HidWindow, await main.HandleEscapeAsync());
    }

    // ---- NavigateTo / RevealTask（INTERFACES 5.8） ----

    [Fact]
    public async Task NavigateAsync_検索中に予定へ_検索を消してビューを切り替える()
    {
        var main = await StartAsync();
        main.UpdateSearchText("資料", isComposing: false);
        await main.ApplySearchAsync();

        await main.NavigateAsync(ViewKey.Upcoming);

        Assert.Equal(ViewKey.Upcoming, main.CurrentView);
        Assert.Equal("", main.SearchText);
        Assert.Null(_f.LastQuery.SearchText);
        Assert.Equal(DueFilter.Next7Days, _f.LastQuery.Due);
    }

    [Fact]
    public async Task NavigateAsync_消えたプロジェクト_移らない()
    {
        var main = await StartAsync();

        await main.NavigateAsync(ViewKey.ForProject(Guid.CreateVersion7()));

        Assert.Equal(ViewKey.Today, main.CurrentView);
    }

    [Fact]
    public async Task RevealTaskAsync_いまの一覧に出ている_ビューを変えずに選んでスクロールし詳細ペインを開く()
    {
        _f.Settings.Current.Appearance.AutoShowDetailPane = false;
        _f.AddRow(_f.NewTask("A", Local(9, 22)));
        var b = _f.NewTask("B", Local(9, 22));
        _f.AddRow(b);
        var main = await StartAsync();
        var scrolled = new List<Guid>();
        main.List.ScrollIntoViewRequested += (_, row) => scrolled.Add(row.Id);

        await main.RevealTaskAsync(b.Id);

        Assert.Equal(ViewKey.Today, main.CurrentView);
        Assert.Equal(b.Id, main.SelectedTaskId);
        Assert.Equal([b.Id], scrolled);
        Assert.True(main.IsDetailVisible);
    }

    [Fact]
    public async Task RevealTaskAsync_一覧に無い古い完了タスク_完了済みビューの全期間で選ぶ()
    {
        _f.AddRow(_f.NewTask("A", Local(9, 22)));
        var done = _f.NewTask("済んだこと", status: TaskItemStatus.Completed);
        done.CompletedAt = FixedClock.LocalToUtc(2026, 3, 1);
        _f.AddRow(done);
        var main = await StartAsync();

        await main.RevealTaskAsync(done.Id);

        Assert.Equal(ViewKey.Completed, main.CurrentView);
        Assert.Null(main.List.CompletedPeriodDays);
        Assert.Equal(done.Id, main.SelectedTaskId);
        Assert.True(main.IsDetailPaneOpen);
    }

    [Fact]
    public async Task RevealTaskAsync_検索中に削除済みのタスク_検索を消してゴミ箱で選ぶ()
    {
        _f.AddRow(_f.NewTask("A", Local(9, 22)));
        var deleted = _f.NewTask("消したもの");
        deleted.DeletedAt = ViewModelFixture.Now;
        _f.AddRow(deleted);
        var main = await StartAsync();
        main.UpdateSearchText("A", isComposing: false);
        await main.ApplySearchAsync();

        await main.RevealTaskAsync(deleted.Id);

        Assert.Equal(ViewKey.Trash, main.CurrentView);
        Assert.Null(main.List.SearchText);
        Assert.Equal(deleted.Id, main.SelectedTaskId);
    }

    [Fact]
    public async Task RevealTaskAsync_見つからないタスク_知らせてビューは変えない()
    {
        var main = await StartAsync();

        await main.RevealTaskAsync(Guid.CreateVersion7());

        Assert.Equal(ViewKey.Today, main.CurrentView);
        Assert.True(main.Toast.IsOpen);
        Assert.False(main.Toast.HasAction);
    }

    // ---- 複数選択（S-10） ----

    private async Task<(MainViewModel Main, TaskItem A, TaskItem B)> SelectTwoAsync()
    {
        var a = _f.NewTask("A", Local(9, 22));
        var b = _f.NewTask("B", Local(9, 22));
        _f.AddRow(a);
        _f.AddRow(b);
        var main = await StartAsync();
        var rows = ViewModelFixture.TaskRows(main.List);
        main.List.SelectedItem = rows[0];
        main.List.SetSelection(rows);
        return (main, a, b);
    }

    [Fact]
    public async Task 複数選択_ShellServiceの選択中タスクに全部入り詳細は主の行のまま()
    {
        var (main, a, b) = await SelectTwoAsync();

        Assert.Equal([a.Id, b.Id], _f.Shell.SelectedTaskIds);
        Assert.Equal(a.Id, main.SelectedTaskId);
        Assert.True(main.List.IsMultiSelection);
    }

    [Fact]
    public async Task HandleEscapeAsync_複数選択中_まず選択を解除する()
    {
        var (main, _, _) = await SelectTwoAsync();

        Assert.Equal(EscapeAction.ClearedSelection, await main.HandleEscapeAsync());

        Assert.False(main.List.IsMultiSelection);
        Assert.Empty(_f.Shell.SelectedTaskIds);
    }

    // ---- サイドバーへのドロップ（UI 設計書 15.5） ----

    [Fact]
    public async Task サイドバーのプロジェクトに落とす_そのプロジェクトへ移してトーストを出す()
    {
        var project = new Project { Name = "業務改善" };
        _f.ProjectList.Add(project);
        var (main, a, b) = await SelectTwoAsync();
        _f.Tasks.UpdateManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(a));
        var item = main.Sidebar.Items.Single(i => i.IsProject);

        main.Sidebar.DropTasks(item, [a.Id, b.Id]);

        Assert.True(await ViewModelFixture.WaitUntilAsync(() => _f.Toasts.Count > 0));
        await _f.Tasks.Received(1).UpdateManyAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { a.Id, b.Id })), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>());
        Assert.Equal(["2 件を「業務改善」へ移しました"], _f.Toasts);
    }

    [Fact]
    public async Task サイドバーのタグに落とす_そのタグを付ける()
    {
        var tag = new Tag { Name = "仕事" };
        _f.TagList.Add(tag);
        var task = _f.NewTask("会議資料", Local(9, 22));
        _f.AddRow(task);
        var main = await StartAsync();
        _f.Tasks.AddTagsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(task));
        var item = main.Sidebar.Items.Single(i => i.IsTag);

        main.Sidebar.DropTasks(item, [task.Id]);

        Assert.True(await ViewModelFixture.WaitUntilAsync(() => _f.Toasts.Count > 0));
        await _f.Tasks.Received(1).AddTagsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == task.Id),
            Arg.Is<IReadOnlyList<Guid>>(tags => tags.Single() == tag.Id),
            Arg.Any<CancellationToken>());
        Assert.Equal(["「会議資料」にタグ「仕事」を付けました"], _f.Toasts);
    }

    [Fact]
    public async Task サイドバーのビューに落とす_何もしない()
    {
        var (main, a, _) = await SelectTwoAsync();
        var today = main.Sidebar.Items.First(i => i.Key == ViewKey.Today);

        main.Sidebar.DropTasks(today, [a.Id]);

        await _f.Tasks.DidNotReceiveWithAnyArgs().UpdateManyAsync(default!, default!, default);
        await _f.Tasks.DidNotReceiveWithAnyArgs().AddTagsAsync(default!, default!, default);
    }
}

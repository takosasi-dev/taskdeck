using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>
/// 中央の一覧（グループ分け・問い合わせの条件・行の操作と取り消し）。時計は JST 2026-09-22（火）10:00。
/// 波2-E の操作（階層・複数選択・一括・ドラッグ・テンプレートの一群）は TaskListViewModelTests.*.cs に分けてある。
/// </summary>
public partial class TaskListViewModelTests
{
    private readonly ViewModelFixture _f = new();

    private static DateTime Local(int month, int day, int hour = 0, int minute = 0) => new(2026, month, day, hour, minute, 0);

    // ---- グループ分け ----

    [Fact]
    public async Task ShowAsync_今日ビュー_期限切れと今日の2グループに分ける()
    {
        var list = _f.CreateList();
        _f.AddRow(_f.NewTask("請求書を出す", Local(9, 19)));
        _f.AddRow(_f.NewTask("会議資料まとめる", Local(9, 22, 15), hasTime: true));
        _f.AddRow(_f.NewTask("週次の買い出し", Local(9, 22)));

        await list.ShowAsync(ViewKey.Today);

        var headers = list.Rows.OfType<TaskGroupHeaderViewModel>().ToList();
        Assert.Equal(["期限切れ", "今日"], headers.Select(h => h.Title));
        Assert.True(headers[0].IsWarning);
        Assert.Equal(1, headers[0].Count);
        Assert.Equal(2, headers[1].Count);
        Assert.IsType<TaskGroupHeaderViewModel>(list.Rows[0]);
        Assert.Equal("請求書を出す", Assert.IsType<TaskRowViewModel>(list.Rows[1]).Title);
        Assert.Same(list.AddRow, list.Rows[^1]);
    }

    [Fact]
    public async Task ShowAsync_予定ビュー_日付ごとに見出しを入れる()
    {
        var list = _f.CreateList();
        _f.AddRow(_f.NewTask("A", Local(9, 22)));
        _f.AddRow(_f.NewTask("B", Local(9, 23)));
        _f.AddRow(_f.NewTask("C", Local(9, 23, 18), hasTime: true));

        await list.ShowAsync(ViewKey.Upcoming);

        var headers = list.Rows.OfType<TaskGroupHeaderViewModel>().ToList();
        Assert.Equal(["今日・9月22日（火）", "明日・9月23日（水）"], headers.Select(h => h.Title));
        Assert.Equal([1, 2], headers.Select(h => h.Count));
    }

    [Fact]
    public async Task ShowAsync_すべてビュー_見出しを入れず親の下に子を字下げする()
    {
        var list = _f.CreateList();
        var parent = _f.NewTask("机まわりを片づける");
        _f.AddRow(parent);
        _f.AddRow(_f.NewTask("ケーブルをまとめる", parentId: parent.Id), isContext: true);

        await list.ShowAsync(ViewKey.All);

        Assert.Empty(list.Rows.OfType<TaskGroupHeaderViewModel>());
        var rows = ViewModelFixture.TaskRows(list);
        Assert.Equal([0, 1], rows.Select(r => r.Level));
        Assert.Equal(34, rows[1].RowHeight);
        Assert.Equal(42, rows[0].RowHeight);
    }

    [Fact]
    public async Task ShowAsync_今日ビュー_残り件数は添えた子を数えない()
    {
        var list = _f.CreateList();
        var parent = _f.NewTask("会議資料まとめる", Local(9, 22));
        _f.AddRow(parent);
        _f.AddRow(_f.NewTask("素材を集める", parentId: parent.Id), isContext: true);
        _f.AddRow(_f.NewTask("週次の買い出し", Local(9, 22)));

        await list.ShowAsync(ViewKey.Today);

        Assert.Equal("9月22日（火）・残り 2 件", list.Subtitle);
    }

    [Fact]
    public async Task ShowAsync_完了済みビュー_既定は過去30日で閉じた日ごとに分ける()
    {
        var list = _f.CreateList();
        _f.AddRow(_f.NewTask("済んだこと", status: TaskItemStatus.Completed));

        await list.ShowAsync(ViewKey.Completed);

        Assert.Equal(new DateOnly(2026, 8, 24), _f.LastQuery.ClosedFrom);
        Assert.Equal(TaskSortKey.Completed, _f.LastQuery.SortKey);
        Assert.True(_f.LastQuery.Descending);
        Assert.Equal("今日・9月22日（火）", list.Rows.OfType<TaskGroupHeaderViewModel>().Single().Title);
        Assert.Equal("過去30日・1 件", list.Subtitle);
        Assert.DoesNotContain(list.AddRow, list.Rows);
    }

    // ---- 問い合わせの条件 ----

    [Fact]
    public async Task SetSearchAsync_今日ビュー_ビューの条件に検索語を足し子を添えない()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);

        await list.SetSearchAsync("資料");

        var query = _f.LastQuery;
        Assert.Equal(DueFilter.TodayOrOverdue, query.Due);
        Assert.Equal(TaskStatusFilter.Open, query.Status);
        Assert.Equal("資料", query.SearchText);
        Assert.False(query.IncludeSubtasks);
    }

    [Fact]
    public async Task SetSearchAsync_すべてのタスクから_削除済み以外の全状態を探す()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);

        await list.SetSearchAsync("資料", allTasks: true);

        var query = _f.LastQuery;
        Assert.Equal(TaskStatusFilter.All, query.Status);
        Assert.Equal(DueFilter.Any, query.Due);
        Assert.Equal("資料", query.SearchText);
        Assert.True(list.SearchAllTasks);
    }

    [Fact]
    public async Task SetSearchAsync_検索中の行_出どころと強調語を持つ()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.All);
        _f.AddRow(_f.NewTask("会議資料まとめる", Local(9, 23)));

        await list.SetSearchAsync("ＡＷＳ 資料");

        var row = Assert.Single(ViewModelFixture.TaskRows(list));
        Assert.Equal("予定", row.SearchSource);
        Assert.Equal("aws 資料", row.SearchTerms);
        Assert.Equal("1 件", list.SearchSummary);
        Assert.DoesNotContain(list.AddRow, list.Rows);
    }

    [Fact]
    public async Task SetSearchAsync_メモだけに一致_メモの抜粋を出す()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.All);
        var task = _f.NewTask("レビュー依頼");
        task.Notes = "配布資料のレビューを依頼する";
        _f.AddRow(task);

        await list.SetSearchAsync("資料");

        var row = Assert.Single(ViewModelFixture.TaskRows(list));
        Assert.True(row.HasNoteSnippet);
        Assert.Equal("配布資料のレビューを依頼する", row.DisplayTitle);
    }

    [Fact]
    public async Task ApplyFilterAsync_検索を消しても_絞り込みは残り検索語は残らない()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.All);
        await list.SetSearchAsync("資料");

        await list.ApplyFilterAsync(list.BuildQuery() with { MinPriority = Priority.High });
        await list.SetSearchAsync(null);

        var query = _f.LastQuery;
        Assert.True(list.IsFiltered);
        Assert.Equal(Priority.High, query.MinPriority);
        Assert.Null(query.SearchText);
        Assert.True(query.IncludeSubtasks);
    }

    [Fact]
    public async Task ApplyFilterAsync_ビューと同じ条件_絞り込みを解除する()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.All);
        await list.ApplyFilterAsync(list.BuildQuery() with { MinPriority = Priority.High });

        await list.ApplyFilterAsync(BuiltInViews.QueryFor(ViewKey.All));

        Assert.False(list.IsFiltered);
        Assert.Null(list.ActiveFilter);
    }

    [Fact]
    public async Task SetSortCommand_優先度_既定は高い順で見出しは優先度順()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.All);

        await list.SetSortCommand.ExecuteAsync(list.SortOptions.Single(o => o.Key == TaskSortKey.Priority));

        Assert.Equal(TaskSortKey.Priority, _f.LastQuery.SortKey);
        Assert.True(_f.LastQuery.Descending);
        Assert.Equal("優先度順", list.CurrentSortLabel);

        await list.SetSortDirectionCommand.ExecuteAsync(list.SortDirections.Single(d => !d.Descending));

        Assert.False(_f.LastQuery.Descending);
        Assert.Equal("優先度順（昇順）", list.CurrentSortLabel);
    }

    // ---- 完了・削除と取り消し ----

    [Fact]
    public async Task SetCompletedAsync_繰り返しなし_取り消しに積みトーストは出さない()
    {
        var list = _f.CreateList();
        var task = _f.NewTask("牛乳を買う", Local(9, 22));
        _f.AddRow(task);
        await list.ShowAsync(ViewKey.Today);
        _f.Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(task));
        var row = ViewModelFixture.TaskRows(list).Single();

        await list.SetCompletedAsync(row, completed: true);

        var entry = _f.Stack.Peek();
        Assert.NotNull(entry);
        Assert.Equal(UndoKind.Toggle, entry.Kind);
        Assert.Equal("「牛乳を買う」を完了しました", entry.Label);
        Assert.Empty(_f.Toasts);
        Assert.True(row.IsFading);
    }

    [Fact]
    public async Task SetCompletedAsync_繰り返しあり_次回の日付をトーストに出す()
    {
        var list = _f.CreateList();
        var task = _f.NewTask("週次の買い出し", Local(9, 22));
        _f.AddRow(task);
        await list.ShowAsync(ViewKey.Today);
        var next = _f.NewTask("週次の買い出し", Local(9, 29));
        _f.Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(task, next));

        await list.SetCompletedAsync(ViewModelFixture.TaskRows(list).Single(), completed: true);

        Assert.Equal(["完了しました・次回は 9月29日（火）"], _f.Toasts);
        Assert.Equal(UndoKind.Toggle, _f.Stack.Peek()!.Kind);
    }

    [Fact]
    public async Task SetCompletedAsync_親の下のサブタスク_取り消し線で残るので消さない()
    {
        var list = _f.CreateList();
        var parent = _f.NewTask("会議資料まとめる");
        var child = _f.NewTask("素材を集める", parentId: parent.Id);
        _f.AddRow(parent);
        _f.AddRow(child, isContext: true);
        await list.ShowAsync(ViewKey.All);
        _f.Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(child));
        var row = ViewModelFixture.TaskRows(list).Single(r => r.IsSubtask);

        await list.SetCompletedAsync(row, completed: true);

        Assert.False(row.IsFading);
        Assert.Equal(UndoKind.Toggle, _f.Stack.Peek()!.Kind);
    }

    [Fact]
    public async Task SetCompletedAsync_完了済みビューで戻す_未完了に戻して行を消す()
    {
        var list = _f.CreateList();
        var task = _f.NewTask("済んだこと", status: TaskItemStatus.Completed);
        _f.AddRow(task);
        await list.ShowAsync(ViewKey.Completed);
        _f.Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), false, Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(task));
        var row = ViewModelFixture.TaskRows(list).Single();

        await list.ToggleCompleteCommand.ExecuteAsync(row);

        await _f.Tasks.Received(1).SetCompletedAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == task.Id), false, Arg.Any<CancellationToken>());
        Assert.Equal("「済んだこと」を未完了に戻しました", _f.Stack.Peek()!.Label);
        Assert.True(row.IsFading);
    }

    [Fact]
    public async Task DeleteAsync_ゴミ箱へ移して削除のトーストを出す()
    {
        var list = _f.CreateList();
        var task = _f.NewTask("会議資料まとめる");
        _f.AddRow(task);
        await list.ShowAsync(ViewKey.All);
        _f.Tasks.SoftDeleteAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(task));

        await list.DeleteAsync(task.Id, task.Title);

        Assert.Equal(["「会議資料まとめる」を削除しました"], _f.Toasts);
        Assert.Equal(UndoKind.Delete, _f.Stack.Peek()!.Kind);
    }

    [Fact]
    public async Task DeleteSelected_読み直した後_同じ位置の次の行を選ぶ()
    {
        var list = _f.CreateList();
        var a = _f.NewTask("A");
        var b = _f.NewTask("B");
        var c = _f.NewTask("C");
        _f.AddRow(a);
        var rowB = _f.AddRow(b);
        _f.AddRow(c);
        await list.ShowAsync(ViewKey.All);
        list.SelectedItem = ViewModelFixture.TaskRows(list)[1];
        _f.Tasks.SoftDeleteAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(b));

        await list.DeleteSelectedCommand.ExecuteAsync(null);
        _f.Rows.Remove(rowB);
        await list.ReloadAsync();

        Assert.Equal(c.Id, list.SelectedRow?.Id);
    }

    [Fact]
    public async Task ReloadAsync_選択中の行が外れた_詳細を閉じずに外れたと知らせる()
    {
        var list = _f.CreateList();
        var a = _f.NewTask("A", Local(9, 22));
        var rowA = _f.AddRow(a);
        await list.ShowAsync(ViewKey.Today);
        list.SelectedItem = ViewModelFixture.TaskRows(list)[0];
        var events = new List<ListSelection>();
        list.SelectedTaskChanged += (_, s) => events.Add(s);

        _f.Rows.Remove(rowA);
        await list.ReloadAsync();

        var selection = Assert.Single(events);
        Assert.Null(selection.TaskId);
        Assert.True(selection.DroppedByReload);
    }

    [Fact]
    public async Task ShowAsync_別のビューで選択中の行が無い_外れたとは扱わない()
    {
        var list = _f.CreateList();
        _f.AddRow(_f.NewTask("A", Local(9, 22)));
        await list.ShowAsync(ViewKey.Today);
        list.SelectedItem = ViewModelFixture.TaskRows(list)[0];
        var events = new List<ListSelection>();
        list.SelectedTaskChanged += (_, s) => events.Add(s);
        _f.Rows.Clear();

        await list.ShowAsync(ViewKey.Trash);

        Assert.False(Assert.Single(events).DroppedByReload);
    }

    [Fact]
    public async Task MoveSelectedAsync_手動順で上へ_前の2行の間の値にする()
    {
        var list = _f.CreateList();
        var a = _f.NewTask("A");
        var b = _f.NewTask("B");
        var c = _f.NewTask("C");
        _f.AddRow(a);
        _f.AddRow(b);
        _f.AddRow(c);
        await list.ShowAsync(ViewKey.All);
        list.SelectedItem = ViewModelFixture.TaskRows(list)[2];

        await list.MoveSelectedAsync(up: true);

        await _f.Tasks.Received(1).ReorderAsync(c.Id, SortOrderMath.Between(a.SortOrder, b.SortOrder), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveSelectedAsync_期限順_並びを変えない()
    {
        var list = _f.CreateList();
        _f.AddRow(_f.NewTask("A", Local(9, 22)));
        _f.AddRow(_f.NewTask("B", Local(9, 22)));
        await list.ShowAsync(ViewKey.Today);
        list.SelectedItem = ViewModelFixture.TaskRows(list)[1];

        await list.MoveSelectedAsync(up: true);

        await _f.Tasks.DidNotReceiveWithAnyArgs().ReorderAsync(default, default, default);
    }

    [Fact]
    public async Task CommitRenameAsync_名前を変えて_テキスト編集として取り消しに積む()
    {
        var list = _f.CreateList();
        var task = _f.NewTask("会議資料");
        _f.AddRow(task);
        await list.ShowAsync(ViewKey.All);
        var row = ViewModelFixture.TaskRows(list).Single();
        _f.Tasks.UpdateAsync(task.Id, Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var result = ViewModelFixture.Changed(task);
                call.Arg<Action<TaskItem>>()(task);
                return result;
            });

        list.BeginRenameCommand.Execute(row);
        row.EditText = "  会議資料まとめる ";
        await list.CommitRenameAsync(row);
        await list.CommitRenameAsync(row);

        await _f.Tasks.Received(1).UpdateAsync(task.Id, Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>());
        Assert.Equal("会議資料まとめる", row.Title);
        Assert.False(row.IsEditing);
        Assert.Equal(UndoKind.TextEdit, _f.Stack.Peek()!.Kind);
    }

    // ---- インライン追加 ----

    [Fact]
    public async Task AddFromInputAsync_今日ビューで日付なし_今日の日付のみの期限を足す()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);
        NewTaskRequest? request = null;
        _f.Tasks.AddAsync(Arg.Do<NewTaskRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(_ => ViewModelFixture.Added(_f.NewTask("牛乳を買う", Local(9, 22))));

        await list.AddFromInputAsync("牛乳を買う");

        Assert.NotNull(request);
        Assert.Equal("牛乳を買う", request.Title);
        Assert.Equal(TaskRules.DateOnlyDue(new DateOnly(2026, 9, 22), _f.Clock), request.DueAt);
        Assert.False(request.DueHasTime);
    }

    [Fact]
    public async Task AddFromInputAsync_日付時刻タグ優先度_パーサで分けて登録する()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.All);
        NewTaskRequest? request = null;
        _f.Tasks.AddAsync(Arg.Do<NewTaskRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(_ => ViewModelFixture.Added(_f.NewTask("会議資料まとめる")));

        await list.AddFromInputAsync("会議資料まとめる 明日 15:00 #仕事 !高");

        Assert.NotNull(request);
        Assert.Equal("会議資料まとめる", request.Title);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, 15), request.DueAt);
        Assert.True(request.DueHasTime);
        Assert.Equal(["仕事"], request.TagNames);
        Assert.Equal(Priority.High, request.Priority);
    }

    [Fact]
    public async Task AddFromInputAsync_プロジェクトのビュー_そのプロジェクトに入れる()
    {
        var list = _f.CreateList();
        var projectId = Guid.CreateVersion7();
        await list.ShowAsync(ViewKey.ForProject(projectId));
        NewTaskRequest? request = null;
        _f.Tasks.AddAsync(Arg.Do<NewTaskRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(_ => ViewModelFixture.Added(_f.NewTask("x", projectId: projectId)));

        await list.AddFromInputAsync("議事録を送る");

        Assert.Equal(projectId, request?.ProjectId);
    }

    [Fact]
    public async Task AddFromInputAsync_いまの一覧に出ない_行き先をトーストで知らせる()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);
        _f.Tasks.AddAsync(Arg.Any<NewTaskRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ViewModelFixture.Added(_f.NewTask("牛乳を買う", Local(9, 23, 15), hasTime: true)));

        await list.AddFromInputAsync("牛乳を買う 明日 15:00");

        Assert.Equal(["「牛乳を買う」を追加しました（期限 明日 15:00）"], _f.Toasts);
    }

    [Fact]
    public async Task AddFromInputAsync_空白だけ_登録しない()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);

        Assert.False(await list.AddFromInputAsync("   "));

        await _f.Tasks.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    // ---- 空状態 ----

    [Fact]
    public async Task ShowAsync_今日が空で予定あり_肯定的な文言と予定を見るボタン()
    {
        var list = _f.CreateList();
        list.Counts = ViewCounts.Empty with { AllOpen = 5, Upcoming = 3 };

        await list.ShowAsync(ViewKey.Today);

        Assert.True(list.IsEmpty);
        Assert.Equal("今日のぶんは終わりです", list.EmptyTitle);
        Assert.Equal("明日以降の予定が 3 件あります", list.EmptyMessage);
        Assert.Equal("予定を見る", list.EmptyActionLabel);
    }

    [Fact]
    public async Task ShowAsync_タスクが1件も無い_初回の案内を出す()
    {
        var list = _f.CreateList();

        await list.ShowAsync(ViewKey.Today);

        Assert.True(list.IsFirstRunEmpty);
        Assert.Equal("最初のタスクを入れましょう", list.EmptyTitle);
        Assert.Null(list.EmptyActionLabel);
    }

    [Fact]
    public async Task SetSearchAsync_見つからない_すべてのタスクから探すを出す()
    {
        var list = _f.CreateList();
        await list.ShowAsync(ViewKey.Today);
        var actions = new List<EmptyStateAction>();
        list.EmptyActionInvoked += (_, a) => actions.Add(a);

        await list.SetSearchAsync("棚卸し");
        await list.InvokeEmptyActionCommand.ExecuteAsync(null);

        Assert.Equal("「棚卸し」は見つかりません", list.EmptyTitle);
        Assert.Equal("今日ビューの中だけを探しています", list.EmptyMessage);
        Assert.Equal("すべてのタスクから探す", list.EmptyActionLabel);
        Assert.Equal([EmptyStateAction.SearchAllTasks], actions);
    }

    [Fact]
    public async Task ShowAsync_ゴミ箱が空_空である旨を出し空にするを押せない()
    {
        var list = _f.CreateList();

        await list.ShowAsync(ViewKey.Trash);

        Assert.Equal("ゴミ箱は空です", list.EmptyTitle);
        Assert.Equal("削除したタスクは 30 日で完全に消えます", list.EmptyMessage);
        Assert.False(list.CanPurgeAll);
    }

    // ---- RevealTask のビューの選び方（INTERFACES 5.8） ----

    [Fact]
    public async Task ViewForTaskAsync_状態ごと_未完了はすべて完了と中止は完了済み削除済みはゴミ箱()
    {
        var list = _f.CreateList();
        var open = _f.NewTask("未完了", status: TaskItemStatus.InProgress);
        var cancelled = _f.NewTask("中止", status: TaskItemStatus.Cancelled);
        var deleted = _f.NewTask("削除済み", status: TaskItemStatus.Completed);
        deleted.DeletedAt = ViewModelFixture.Now;
        _f.OtherTasks.AddRange([open, cancelled, deleted]);

        Assert.Equal(ViewKey.All, await list.ViewForTaskAsync(open.Id));
        Assert.Equal(ViewKey.Completed, await list.ViewForTaskAsync(cancelled.Id));
        Assert.Equal(ViewKey.Trash, await list.ViewForTaskAsync(deleted.Id));
        Assert.Null(await list.ViewForTaskAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task ViewQuery_プロジェクトのビューで絞り込み中_絞り込みの前のビューの条件を返す()
    {
        var list = _f.CreateList();
        var projectId = Guid.CreateVersion7();
        await list.ShowAsync(ViewKey.ForProject(projectId));
        await list.ApplyFilterAsync(list.BuildQuery() with { ProjectId = null, MinPriority = Priority.High });

        Assert.Equal(projectId, list.ViewQuery.ProjectId);
        Assert.Null(list.ViewQuery.MinPriority);
    }
}

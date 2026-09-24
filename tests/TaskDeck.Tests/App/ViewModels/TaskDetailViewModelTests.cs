using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>詳細ペイン（表示用の値・その場で保存と取り消し・タグの候補・選び直しの競合）。時計は JST 2026-09-22（火）10:00。</summary>
public class TaskDetailViewModelTests
{
    private readonly ViewModelFixture _f = new();

    private TaskItem Stored(string title = "会議資料まとめる")
    {
        var task = _f.NewTask(title, new DateTime(2026, 9, 22, 15, 0, 0), hasTime: true, priority: Priority.High);
        task.RemindOffsetMinutes = 60;
        task.DurationMinutes = 90;
        return task;
    }

    private void GivenDetail(TaskItem task, IReadOnlyList<Guid>? tagIds = null, RecurrenceRule? rule = null, IReadOnlyList<TaskItem>? children = null) =>
        _f.Tasks.GetDetailAsync(task.Id, Arg.Any<CancellationToken>())
            .Returns(new TaskDetail(task, tagIds ?? [], rule, children ?? []));

    /// <summary>UpdateAsync が mutate をそのタスクに当てて結果を返すようにする。</summary>
    private void GivenUpdatesApply(TaskItem task) =>
        _f.Tasks.UpdateAsync(task.Id, Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var result = ViewModelFixture.Changed(task);
                call.Arg<Action<TaskItem>>()(task);
                return result;
            });

    [Fact]
    public async Task LoadAsync_値を表示用の文言にする()
    {
        var tag = new Tag { Name = "仕事" };
        _f.TagList.Add(tag);
        var task = Stored();
        var done = _f.NewTask("素材を集める", status: TaskItemStatus.Completed, parentId: task.Id);
        var open = _f.NewTask("グラフを作る", parentId: task.Id);
        GivenDetail(task, [tag.Id], new RecurrenceRule { RRule = "FREQ=WEEKLY;BYDAY=MO" }, [done, open]);
        var detail = _f.CreateDetail();

        await detail.LoadAsync(task.Id);

        Assert.True(detail.HasTask);
        Assert.Equal("9月22日（火） 15:00", detail.DueText);
        Assert.Equal("高", detail.PriorityText);
        Assert.Equal("!!!", detail.PrioritySymbol);
        Assert.Equal("1時間前", detail.ReminderText);
        Assert.Equal("1時間30分", detail.DurationText);
        Assert.Equal("毎週 月", detail.RecurrenceText);
        Assert.Equal(["仕事"], detail.Tags.Select(t => t.Name));
        Assert.Equal("1 / 2", detail.SubtaskProgressText);
        Assert.Equal(0.5, detail.SubtaskProgressValue);
        Assert.Equal("作成 9/22 10:00 ・ 更新 9/22 10:00", detail.CreatedUpdatedText);
        Assert.Equal(new DateOnly(2026, 9, 22), detail.DueDate);
        Assert.Equal(new TimeOnly(15, 0), detail.DueTime);
    }

    [Fact]
    public async Task LoadAsync_速く選び直す_最後に選んだタスクだけを出す()
    {
        var first = Stored("A");
        var second = Stored("B");
        var slow = new TaskCompletionSource<TaskDetail?>();
        _f.Tasks.GetDetailAsync(first.Id, Arg.Any<CancellationToken>()).Returns(slow.Task);
        GivenDetail(second);
        var detail = _f.CreateDetail();

        var loadingFirst = detail.LoadAsync(first.Id);
        await detail.LoadAsync(second.Id);
        slow.SetResult(new TaskDetail(first, [], null, []));
        await loadingFirst;

        Assert.Equal(second.Id, detail.TaskId);
        Assert.Equal("B", detail.Title);
    }

    [Fact]
    public async Task ApplyDueAsync_日付だけ_その日のローカル0時で保存し取り消しに積む()
    {
        var task = Stored();
        GivenDetail(task);
        GivenUpdatesApply(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        await detail.ApplyDueAsync(new DateOnly(2026, 9, 25), null);

        Assert.Equal(TaskRules.DateOnlyDue(new DateOnly(2026, 9, 25), _f.Clock), task.DueAt);
        Assert.False(task.DueHasTime);
        Assert.Equal("期限を変えました", _f.Stack.Peek()!.Label);
    }

    [Fact]
    public async Task ApplyDueAsync_日付なし_期限を消す()
    {
        var task = Stored();
        GivenDetail(task);
        GivenUpdatesApply(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        await detail.ApplyDueAsync(null, null);

        Assert.Null(task.DueAt);
        Assert.Equal("期限を消しました", _f.Stack.Peek()!.Label);
    }

    [Fact]
    public async Task CommitTitleAsync_空にした_保存せず元の名前に戻す()
    {
        var task = Stored();
        GivenDetail(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        await detail.CommitTitleAsync("   ");

        await _f.Tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        Assert.Equal("会議資料まとめる", detail.Title);
    }

    [Fact]
    public async Task CommitTitleAsync_名前を変える_テキスト編集として取り消しに積む()
    {
        var task = Stored();
        GivenDetail(task);
        GivenUpdatesApply(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        await detail.CommitTitleAsync("会議資料をまとめて送る");

        Assert.Equal("会議資料をまとめて送る", detail.Title);
        Assert.Equal(UndoKind.TextEdit, _f.Stack.Peek()!.Kind);
    }

    [Fact]
    public async Task CommitTitleAsync_保存中に別のタスクを選んだ_新しいタスクの表示を書き換えない()
    {
        var task = Stored("A");
        var other = Stored("B");
        GivenDetail(task);
        GivenDetail(other);
        var saving = new TaskCompletionSource<TaskMutationResult>();
        _f.Tasks.UpdateAsync(task.Id, Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>()).Returns(saving.Task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        var committing = detail.CommitTitleAsync("A2");
        await detail.LoadAsync(other.Id);
        saving.SetResult(ViewModelFixture.Changed(task));
        await committing;

        Assert.Equal("B", detail.Title);
    }

    [Fact]
    public async Task AddTagAsync_無いタグ_作って今のタグに足す()
    {
        var existing = new Tag { Name = "仕事" };
        var created = new Tag { Name = "買い物" };
        _f.TagList.Add(existing);
        var task = Stored();
        GivenDetail(task, [existing.Id]);
        _f.Tags.GetOrCreateAsync("買い物", Arg.Any<CancellationToken>()).Returns(created);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        await detail.AddTagAsync("＃買い物");

        await _f.Tasks.Received(1).SetTagsAsync(
            task.Id,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { existing.Id, created.Id })),
            Arg.Any<CancellationToken>());
        Assert.Equal("", detail.TagInput);
    }

    [Fact]
    public async Task UpdateTagSuggestions_同名が無い_既存の候補と新しく作る候補を出す()
    {
        _f.TagList.Add(new Tag { Name = "会議" });
        _f.TagList.Add(new Tag { Name = "仕事" });
        var task = Stored();
        GivenDetail(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        detail.TagInput = "会";
        detail.UpdateTagSuggestions();

        Assert.Equal(["#会議", "＋「会」を作る"], detail.TagSuggestions.Select(s => s.Label));
        Assert.True(detail.IsTagSuggestionOpen);
    }

    [Fact]
    public async Task UpdateTagSuggestions_同名がある_新しく作る候補を出さない()
    {
        _f.TagList.Add(new Tag { Name = "仕事" });
        var task = Stored();
        GivenDetail(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        detail.TagInput = "#仕事";
        detail.UpdateTagSuggestions();

        Assert.Equal(["#仕事"], detail.TagSuggestions.Select(s => s.Label));
    }

    [Fact]
    public async Task LoadAsync_期限なし_通知は選べない()
    {
        var task = _f.NewTask("いつかやる");
        GivenDetail(task);
        var detail = _f.CreateDetail();

        await detail.LoadAsync(task.Id);

        Assert.False(detail.CanSetReminder);
        Assert.Equal("なし", detail.DueText);
    }

    [Fact]
    public async Task SetStatusCommand_繰り返しを完了にする_次回の日付をトーストに出す()
    {
        var task = Stored("週次の買い出し");
        GivenDetail(task);
        var next = _f.NewTask("週次の買い出し", new DateTime(2026, 9, 29, 0, 0, 0));
        _f.Tasks.UpdateAsync(task.Id, Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>())
            .Returns(ViewModelFixture.Changed(task, next));
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        await detail.SetStatusCommand.ExecuteAsync(detail.StatusOptions.Single(o => o.Value == TaskItemStatus.Completed));

        Assert.Equal(["完了しました・次回は 9月29日（火）"], _f.Toasts);
    }

    [Fact]
    public async Task DeleteCommand_削除を一覧に頼む()
    {
        var task = Stored();
        GivenDetail(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);
        var requests = new List<(Guid Id, string Title)>();
        detail.DeleteRequested += (_, r) => requests.Add(r);

        detail.DeleteCommand.Execute(null);

        Assert.Equal([(task.Id, "会議資料まとめる")], requests);
    }

    // ---- 所要時間（カレンダーで選択肢に無い値が入る） ----

    [Fact]
    public async Task LoadAsync_選択肢に無い所要時間_時間と分で出す()
    {
        var task = Stored();
        task.DurationMinutes = 75;
        GivenDetail(task);
        var detail = _f.CreateDetail();

        await detail.LoadAsync(task.Id);

        Assert.Equal("1時間15分", detail.DurationText);
        Assert.Equal(["なし", "15分", "30分", "45分", "1時間", "1時間30分", "2時間"], detail.DurationOptions.Select(o => o.Label));
    }

    [Fact]
    public async Task SetDurationCommand_選択肢に無い値から選び直す_選んだ値にする()
    {
        var task = Stored();
        task.DurationMinutes = 105;
        GivenDetail(task);
        GivenUpdatesApply(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        await detail.SetDurationCommand.ExecuteAsync(detail.DurationOptions.Single(o => o.Value == 60));

        Assert.Equal(60, task.DurationMinutes);
        Assert.Equal("所要時間を「1時間」にしました", _f.Stack.Peek()!.Label);
    }

    // ---- サブタスク（F-021〜F-024） ----

    [Fact]
    public async Task AddSubtaskAsync_入力あり_親とプロジェクトを指定して足し入力欄を空にする()
    {
        var projectId = Guid.CreateVersion7();
        var task = _f.NewTask("会議資料まとめる", projectId: projectId);
        GivenDetail(task);
        NewTaskRequest? request = null;
        _f.Tasks.AddAsync(Arg.Do<NewTaskRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(_ => ViewModelFixture.Added(_f.NewTask("グラフを作る", parentId: task.Id)));
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        detail.SubtaskInput = "  グラフを作る ";
        await detail.AddSubtaskAsync();

        Assert.NotNull(request);
        Assert.Equal("グラフを作る", request.Title);
        Assert.Equal(task.Id, request.ParentTaskId);
        Assert.Equal(projectId, request.ProjectId);
        Assert.Equal("", detail.SubtaskInput);
        Assert.Equal("サブタスク「グラフを作る」を追加しました", _f.Stack.Peek()!.Label);
    }

    [Fact]
    public async Task AddSubtaskAsync_空白だけ_足さない()
    {
        var task = Stored();
        GivenDetail(task);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);

        detail.SubtaskInput = "   ";
        await detail.AddSubtaskAsync();

        await _f.Tasks.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task LoadAsync_3階層目のタスク_サブタスクは足せず欄も出さない()
    {
        var task = Stored();
        task.Depth = TaskItem.MaxDepth;
        GivenDetail(task);
        var detail = _f.CreateDetail();

        await detail.LoadAsync(task.Id);

        Assert.False(detail.CanAddSubtask);
        Assert.False(detail.ShowSubtaskSection);
    }

    [Fact]
    public async Task LoadAsync_別のタスクを選ぶ_書きかけのサブタスク名を持ち越さない()
    {
        var first = Stored("A");
        var second = Stored("B");
        GivenDetail(first);
        GivenDetail(second);
        var detail = _f.CreateDetail();
        await detail.LoadAsync(first.Id);
        detail.SubtaskInput = "書きかけ";

        await detail.LoadAsync(second.Id);

        Assert.Equal("", detail.SubtaskInput);
        Assert.True(detail.ShowSubtaskSection);
    }

    [Fact]
    public async Task SetStatusCommand_未完了の子がいる親を完了_確認してはいならまとめて完了にする()
    {
        var task = Stored();
        var open = _f.NewTask("グラフを作る", parentId: task.Id);
        GivenDetail(task, children: [open]);
        _f.Tasks.GetDetailAsync(open.Id, Arg.Any<CancellationToken>()).Returns(new TaskDetail(open, [], null, []));
        var detail = _f.CreateDetail();
        await detail.LoadAsync(task.Id);
        var asked = 0;
        detail.Confirm = _ =>
        {
            asked++;
            return ConfirmChoice.Yes;
        };

        await detail.SetStatusCommand.ExecuteAsync(detail.StatusOptions.Single(o => o.Value == TaskItemStatus.Completed));

        Assert.Equal(1, asked);
        await _f.Tasks.Received(1).SetCompletedAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { task.Id, open.Id })), true, Arg.Any<CancellationToken>());
        await _f.Tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }
}

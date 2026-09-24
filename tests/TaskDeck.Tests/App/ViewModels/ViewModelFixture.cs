using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views.Scratch;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Results;
using TaskDeck.Core.Scratch;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.Scratch;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>UI スレッドの代わりにその場で実行する。</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>
/// メイン画面の ViewModel 一式を、リポジトリを NSubstitute にして組み立てる。時計は JST 2026-09-22（火）10:00。
/// 一覧の問い合わせは Rows に入れた行を返し、渡された条件は Queries に残る。
/// </summary>
internal sealed class ViewModelFixture
{
    public static readonly DateTime Now = FixedClock.LocalToUtc(2026, 9, 22, 10, 0);

    public FixedClock Clock { get; } = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    public ITaskRepository Tasks { get; } = Substitute.For<ITaskRepository>();

    public IProjectRepository Projects { get; } = Substitute.For<IProjectRepository>();

    public ITagRepository Tags { get; } = Substitute.For<ITagRepository>();

    public ITemplateRepository Templates { get; } = Substitute.For<ITemplateRepository>();

    /// <summary>テンプレートと項目（一群の見出しの名前を当てるのに使う）。</summary>
    public List<TemplateWithItems> TemplateList { get; } = [];

    public FakeSettingsStore Settings { get; } = new();

    public UndoStack Stack { get; } = new();

    public DataChangeHub Hub { get; } = new();

    public List<TaskListRow> Rows { get; } = [];

    public List<Project> ProjectList { get; } = [];

    public List<Tag> TagList { get; } = [];

    public List<TaskQuery> Queries { get; } = [];

    public List<string> Toasts { get; } = [];

    public ViewCounts Counts { get; set; } = ViewCounts.Empty;

    public UndoService Undo { get; }

    public ThemeService Theme { get; }

    public ShellService Shell { get; }

    /// <summary>使い捨てリスト（保存先はメモリ上の AppState）。</summary>
    public MemoryAppState AppState { get; } = new();

    public ScratchStore ScratchStore { get; }

    public ScratchPresenter ScratchPresenter { get; }

    public ViewModelFixture()
    {
        Undo = new UndoService(Tasks, Stack, NullLogger<UndoService>.Instance);
        Undo.ToastRequested += (_, e) => Toasts.Add(e.Message);
        Theme = new ThemeService(Settings);
        Shell = new ShellService(Substitute.For<IServiceProvider>(), Clock, NullLogger<ShellService>.Instance);
        ScratchStore = new ScratchStore(AppState, Clock);
        ScratchPresenter = new ScratchPresenter(ScratchStore, AppState, Templates, Shell, Substitute.For<IServiceProvider>(), NullLogger<ScratchPresenter>.Instance);

        // 状態と完了済みの期間だけはまねる（完了済み・ゴミ箱のビューに未完了の行が出ないように）。添えた子（IsContext）は条件によらず出す
        Tasks.QueryAsync(Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<TaskQuery>();
                Queries.Add(query);
                return Task.FromResult<IReadOnlyList<TaskListRow>>([.. Rows.Where(r => r.IsContext || Matches(query, r.Task))]);
            });
        Tasks.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<Guid>();
                return Rows.Select(r => r.Task).Concat(OtherTasks).FirstOrDefault(t => t.Id == id);
            });
        Tasks.GetViewCountsAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Counts));
        Projects.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Project>>([.. ProjectList]));
        Tags.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Tag>>([.. TagList]));
        Templates.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<TemplateSummary>>([.. TemplateList.Select(t => new TemplateSummary(t.Template, t.Items.Count))]));
        Templates.GetWithItemsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => TemplateList.FirstOrDefault(t => t.Template.Id == call.Arg<Guid>()));

        // 書き込みは既定で「何も変わらなかった」を返す（テストごとに必要なものだけ上書きする）
        var none = Task.FromResult(TaskMutationResult.None);
        Tasks.AddAsync(Arg.Any<NewTaskRequest>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.UpdateAsync(Arg.Any<Guid>(), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.SetTagsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.SetRecurrenceAsync(Arg.Any<Guid>(), Arg.Any<RecurrenceInput?>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.SoftDeleteAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.RestoreAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.ReorderAsync(Arg.Any<Guid>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.UpdateManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.AddTagsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.DuplicateAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.SetParentAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OperationResult<TaskMutationResult>.Success(TaskMutationResult.None)));
    }

    public TaskQuery LastQuery => Queries[^1];

    /// <summary>一覧の問い合わせには出ないが GetAsync で引けるタスク（別のビューにあるもの）。</summary>
    public List<TaskItem> OtherTasks { get; } = [];

    private bool Matches(TaskQuery query, TaskItem task)
    {
        if (query.ClosedFrom is { } from && (task.CompletedAt is not { } closed || Clock.ToLocalDate(closed) < from))
        {
            return false;
        }
        return query.Status switch
        {
            TaskStatusFilter.Open => !task.IsDeleted && task.IsOpen,
            TaskStatusFilter.Done => !task.IsDeleted && !task.IsOpen,
            TaskStatusFilter.Deleted => task.IsDeleted,
            _ => !task.IsDeleted,
        };
    }

    // スケルトンは出さない（UI スレッドの無いテストでは、待ちの続きが読み込みと別のスレッドで走り、重いときに行を消してしまう）
    public TaskListViewModel CreateList() =>
        new(Tasks, Projects, Tags, Templates, Settings, Clock, Undo, Theme, new QuickInputParser(Clock), NullLogger<TaskListViewModel>.Instance)
        {
            SkeletonDelay = null,
        };

    public SidebarViewModel CreateSidebar() =>
        new(Projects, Tags, Tasks, Settings, Theme, ScratchStore, NullLogger<SidebarViewModel>.Instance);

    public TaskDetailViewModel CreateDetail() => new(Tasks, Projects, Tags, Clock, Undo, Theme);

    public MainViewModel CreateMain(TaskListViewModel? list = null) =>
        new(
            CreateSidebar(),
            list ?? CreateList(),
            CreateDetail(),
            new ToastViewModel(),
            new ScratchPaneViewModel(ScratchPresenter),
            ScratchStore,
            Settings,
            Hub,
            Undo,
            Shell,
            new ImmediateDispatcher(),
            NullLogger<MainViewModel>.Instance);

    // ---- データを作る ----

    private double _order;

    /// <summary>未完了のタスク。dueLocal はローカル日時（hasTime=false なら日付だけ使う）。</summary>
    public TaskItem NewTask(
        string title,
        DateTime? dueLocal = null,
        bool hasTime = false,
        Priority priority = Priority.None,
        TaskItemStatus status = TaskItemStatus.NotStarted,
        Guid? parentId = null,
        Guid? projectId = null)
    {
        DateTime? dueUtc = dueLocal is { } local
            ? hasTime
                ? FixedClock.LocalToUtc(local.Year, local.Month, local.Day, local.Hour, local.Minute)
                : FixedClock.LocalToUtc(local.Year, local.Month, local.Day)
            : null;
        return new TaskItem
        {
            Title = title,
            DueAt = dueUtc,
            DueHasTime = dueUtc is not null && hasTime,
            Priority = priority,
            Status = status,
            CompletedAt = status is TaskItemStatus.Completed or TaskItemStatus.Cancelled ? Now : null,
            ParentTaskId = parentId,
            Depth = parentId is null ? 0 : 1,
            ProjectId = projectId,
            SortOrder = _order += SortOrderMath.Step,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
    }

    /// <summary>一覧の行を足す。subtasks / subtasksDone は直下の子の数と、そのうち閉じた数（進捗・親の完了の確認に使う）。</summary>
    public TaskListRow AddRow(TaskItem task, bool isContext = false, IReadOnlyList<Guid>? tagIds = null, int subtasks = 0, int subtasksDone = 0)
    {
        var row = new TaskListRow(task, tagIds ?? [], subtasks, subtasksDone, isContext);
        Rows.Add(row);
        return row;
    }

    /// <summary>詳細（直下の子）を返すようにする（親の完了の確認で子孫を引くため）。</summary>
    public void GivenChildren(TaskItem parent, params TaskItem[] children) =>
        Tasks.GetDetailAsync(parent.Id, Arg.Any<CancellationToken>()).Returns(new TaskDetail(parent, [], null, children));

    /// <summary>書き込みの結果（取り消せる変更が1件あるもの）。</summary>
    public static TaskMutationResult Changed(TaskItem task, params TaskItem[] created) =>
        new(new ChangeSet([new TaskSnapshot(task.Clone(), [])], [.. created.Select(t => t.Id)], []), [task], created);

    /// <summary>追加の結果（取り消すと作ったタスクが消える）。</summary>
    public static TaskMutationResult Added(TaskItem task) => new(new ChangeSet([], [task.Id], []), [task], [task]);

    public static IReadOnlyList<TaskRowViewModel> TaskRows(TaskListViewModel list) => [.. list.Rows.OfType<TaskRowViewModel>()];

    /// <summary>
    /// イベント経由（async void）で進む処理を待つ。5秒たっても満たされなければ false（全体を並列で流して重いときにも足りるように）。
    /// 裏のスレッド（UI スレッドの無いテストの続き・間引きのタイマー）が作り直している最中のコレクションを読むと例外になるので、そのときは「まだ」とみなす。
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Holds(condition))
            {
                return true;
            }
            await System.Threading.Tasks.Task.Delay(20);
        }
        return Holds(condition);
    }

    private static bool Holds(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

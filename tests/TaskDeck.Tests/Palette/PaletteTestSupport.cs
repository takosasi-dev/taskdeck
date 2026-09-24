using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Focus;
using TaskDeck.App.Views.Palette;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Palette;

/// <summary>AppState をメモリに持つ（パレットの使用頻度・最近開いたタスクの読み書きを確かめる）。</summary>
internal sealed class PaletteFakeAppState : IAppStateRepository
{
    public Dictionary<string, string> Values { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        if (value is null)
        {
            Values.Remove(key);
        }
        else
        {
            Values[key] = value;
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// パレット・フォーカスモードの ViewModel を、リポジトリを NSubstitute にして組み立てる。時計は JST 2026-09-22（火）10:00。
/// QueryAsync は Rows のうち条件（状態・プロジェクト・タグ・検索語・期限・閉じた日）に合うものを返し、渡された条件は Queries に残る。
/// </summary>
internal sealed class PaletteFixture
{
    public static readonly DateTime Now = FixedClock.LocalToUtc(2026, 9, 22, 10, 0);

    private double _order;

    public PaletteFixture()
    {
        Undo = new UndoService(Tasks, Stack, NullLogger<UndoService>.Instance);
        Undo.ToastRequested += (_, e) => Toasts.Add(e.Message);

        Tasks.QueryAsync(Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<TaskQuery>();
                Queries.Add(query);
                return Task.FromResult<IReadOnlyList<TaskListRow>>([.. Rows.Where(r => Matches(query, r))]);
            });
        Tasks.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                return Task.FromResult<IReadOnlyList<TaskItem>>([.. Rows.Select(r => r.Task).Where(t => ids.Contains(t.Id))]);
            });
        Tasks.GetViewCountsAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Counts));
        Projects.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Project>>([.. ProjectList]));
        Tags.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<Tag>>([.. TagList]));
        Templates.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<TemplateSummary>>([.. TemplateList.Select(t => new TemplateSummary(t.Template, t.Items.Count))]));
        Templates.GetWithItemsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => TemplateList.FirstOrDefault(t => t.Template.Id == call.Arg<Guid>()));
        // 詳細は Rows から組み立てる（子は ParentTaskId が一致するもの。テストで個別に上書きしてよい）
        Tasks.GetDetailAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<Guid>();
                return Rows.FirstOrDefault(r => r.Task.Id == id) is { } row
                    ? new TaskDetail(row.Task, row.TagIds, null, [.. Rows.Select(r => r.Task).Where(t => t.ParentTaskId == id && !t.IsDeleted)])
                    : null;
            });

        var none = Task.FromResult(TaskMutationResult.None);
        Tasks.AddAsync(Arg.Any<NewTaskRequest>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(none);
        Tasks.CarryOverOverdueAsync(Arg.Any<CancellationToken>()).Returns(none);
        Tasks.SoftDeleteAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(none);
        Templates.ExpandAsync(Arg.Any<ExpansionPlan>(), Arg.Any<CancellationToken>()).Returns(none);
    }

    public FixedClock Clock { get; } = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    public ITaskRepository Tasks { get; } = Substitute.For<ITaskRepository>();

    public IProjectRepository Projects { get; } = Substitute.For<IProjectRepository>();

    public ITagRepository Tags { get; } = Substitute.For<ITagRepository>();

    public ITemplateRepository Templates { get; } = Substitute.For<ITemplateRepository>();

    public PaletteFakeAppState AppState { get; } = new();

    public IPaletteHost Host { get; } = Substitute.For<IPaletteHost>();

    public FakeSettingsStore Settings { get; } = new();

    public UndoStack Stack { get; } = new();

    public UndoService Undo { get; }

    public List<TaskListRow> Rows { get; } = [];

    public List<Project> ProjectList { get; } = [];

    public List<Tag> TagList { get; } = [];

    public List<TemplateWithItems> TemplateList { get; } = [];

    public List<TaskQuery> Queries { get; } = [];

    public List<string> Toasts { get; } = [];

    public ViewCounts Counts { get; set; } = ViewCounts.Empty;

    public CommandPaletteViewModel CreatePalette() =>
        new(Tasks, Projects, Tags, Templates, AppState, Settings, Clock, Undo, new QuickInputParser(Clock), new TemplateExpander(Clock), Host,
            NullLogger<CommandPaletteViewModel>.Instance);

    public FocusViewModel CreateFocus() => new(Tasks, Projects, Tags, Clock, Undo, NullLogger<FocusViewModel>.Instance);

    // ---- データを作る ----

    /// <summary>タスクを作って Rows に入れる。dueLocal はローカル日時（hasTime=false なら日付だけ使う）。</summary>
    public TaskItem AddTask(
        string title,
        DateTime? dueLocal = null,
        bool hasTime = false,
        Priority priority = Priority.None,
        TaskItemStatus status = TaskItemStatus.NotStarted,
        Guid? projectId = null,
        IReadOnlyList<Guid>? tagIds = null,
        string? notes = null,
        DateTime? createdUtc = null,
        DateTime? completedUtc = null,
        Guid? parentId = null)
    {
        DateTime? dueUtc = dueLocal is { } local
            ? hasTime
                ? FixedClock.LocalToUtc(local.Year, local.Month, local.Day, local.Hour, local.Minute)
                : FixedClock.LocalToUtc(local.Year, local.Month, local.Day)
            : null;
        var task = new TaskItem
        {
            Title = title,
            Notes = notes,
            DueAt = dueUtc,
            DueHasTime = dueUtc is not null && hasTime,
            Priority = priority,
            Status = status,
            CompletedAt = status is TaskItemStatus.Completed or TaskItemStatus.Cancelled ? completedUtc ?? Now : null,
            ProjectId = projectId,
            ParentTaskId = parentId,
            Depth = parentId is null ? 0 : 1,
            SortOrder = _order += SortOrderMath.Step,
            CreatedAt = createdUtc ?? Now,
            UpdatedAt = Now,
        };
        Rows.Add(new TaskListRow(task, tagIds ?? [], 0, 0, false));
        return task;
    }

    public Project AddProject(string name, string color = "#0067C0")
    {
        var project = new Project { Name = name, ColorHex = color, SortOrder = ProjectList.Count + 1 };
        ProjectList.Add(project);
        return project;
    }

    public Tag AddTag(string name)
    {
        var tag = new Tag { Name = name };
        TagList.Add(tag);
        return tag;
    }

    /// <summary>テンプレート（項目は期限の相対日数）。</summary>
    public TemplateWithItems AddTemplate(string name, params (string Title, int? Offset)[] items)
    {
        var template = new TaskTemplate { Name = name };
        var list = items
            .Select((item, i) => new TaskTemplateItem { TemplateId = template.Id, Title = item.Title, DueOffsetDays = item.Offset, SortOrder = i + 1 })
            .ToList();
        var entry = new TemplateWithItems(template, list);
        TemplateList.Add(entry);
        return entry;
    }

    /// <summary>書き込みの結果（取り消せる変更が1件あるもの）。</summary>
    public static TaskMutationResult Changed(TaskItem task, params TaskItem[] created) =>
        new(new ChangeSet([new TaskSnapshot(task.Clone(), [])], [.. created.Select(t => t.Id)], []), [task], created);

    /// <summary>作成の結果（取り消すと作ったタスクが消える）。</summary>
    public static TaskMutationResult Added(params TaskItem[] tasks) => new(new ChangeSet([], [.. tasks.Select(t => t.Id)], []), tasks, tasks);

    /// <summary>行の見出しと選べる行を「見出し: 行, 行」の形の文字列にする（並びを1行で比べる）。</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<PaletteRow> rows)
    {
        var lines = new List<string>();
        foreach (var row in rows)
        {
            switch (row)
            {
                case PaletteHeaderRow header:
                    lines.Add("# " + header.Title);
                    break;
                case PaletteTaskItem task:
                    lines.Add(task.Title);
                    break;
                case PaletteItem item:
                    lines.Add(item.Label);
                    break;
                case PaletteNoteRow note:
                    lines.Add("- " + note.Text);
                    break;
            }
        }
        return lines;
    }

    private bool Matches(TaskQuery query, TaskListRow row)
    {
        var task = row.Task;
        var statusOk = query.Statuses.Count > 0
            ? !task.IsDeleted && query.Statuses.Contains(task.Status)
            : query.Status switch
            {
                TaskStatusFilter.Open => !task.IsDeleted && task.IsOpen,
                TaskStatusFilter.Done => !task.IsDeleted && !task.IsOpen,
                TaskStatusFilter.Deleted => task.IsDeleted,
                _ => !task.IsDeleted,
            };
        if (!statusOk)
        {
            return false;
        }
        if (query.ProjectId is { } projectId && task.ProjectId != projectId)
        {
            return false;
        }
        if (query.TagIds.Any(id => !row.TagIds.Contains(id)))
        {
            return false;
        }
        if (query.Due == DueFilter.TodayOrOverdue
            && (task.DueAt is not { } due || due >= FixedClock.LocalToUtc(2026, 9, 23)))
        {
            return false;
        }
        if (query.ClosedFrom is { } from && (task.CompletedAt is not { } closed || closed < FixedClock.LocalToUtc(from.Year, from.Month, from.Day)))
        {
            return false;
        }
        if (query.HasSearch)
        {
            var key = TextNormalizer.SearchKey(task.Title, task.Notes);
            return TextNormalizer.ForSearch(query.SearchText).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => key.Contains(t, StringComparison.Ordinal));
        }
        return true;
    }
}

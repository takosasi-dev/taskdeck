using TaskDeck.Core.Entities;

namespace TaskDeck.Core.Abstractions;

/// <summary>一覧の1行ぶん。IsContext=true は「条件には合わないが、合ったタスクの子孫なので添えた行」。</summary>
public sealed record TaskListRow(
    TaskItem Task,
    IReadOnlyList<Guid> TagIds,
    int SubtaskCount,
    int SubtaskDoneCount,
    bool IsContext);

/// <summary>詳細ペイン用。Children は削除済みを除く直下の子（SortOrder 順）。</summary>
public sealed record TaskDetail(
    TaskItem Task,
    IReadOnlyList<Guid> TagIds,
    RecurrenceRule? Rule,
    IReadOnlyList<TaskItem> Children);

/// <summary>取り消し用に保存する、変更前のタスク1件（全列の複製＋その時点の有効なタグ）。</summary>
public sealed record TaskSnapshot(TaskItem Task, IReadOnlyList<Guid> TagIds);

/// <summary>
/// 1回の書き込みで変わったもの。取り消しは ITaskRepository.ApplyUndoAsync に渡すだけで戻る:
/// TasksBefore の状態に書き戻し、CreatedTaskIds を消し（未同期なら物理削除、同期済みならソフトデリート）、RulesBefore を書き戻す。
/// </summary>
public sealed record ChangeSet(
    IReadOnlyList<TaskSnapshot> TasksBefore,
    IReadOnlyList<Guid> CreatedTaskIds,
    IReadOnlyList<RecurrenceRule> RulesBefore)
{
    public static ChangeSet Empty { get; } = new([], [], []);

    public bool IsEmpty => TasksBefore.Count == 0 && CreatedTaskIds.Count == 0 && RulesBefore.Count == 0;
}

/// <summary>書き込みの結果。Affected は書き込み後の状態、Created は新しく作られたタスク（繰り返しの次回を含む）。</summary>
public sealed record TaskMutationResult(
    ChangeSet Changes,
    IReadOnlyList<TaskItem> Affected,
    IReadOnlyList<TaskItem> Created)
{
    public static TaskMutationResult None { get; } = new(ChangeSet.Empty, [], []);
}

/// <summary>
/// 新しいタスクの入力。ProjectName / TagNames は同じトランザクションで名前から引き、無ければ作る（F-044）。
/// DueAt は UTC（日付のみならローカル 0:00 の UTC）。SortOrder が null なら兄弟の末尾に置く。
/// </summary>
public sealed record NewTaskRequest
{
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public TaskItemStatus Status { get; init; } = TaskItemStatus.NotStarted;
    public Priority Priority { get; init; }
    public DateTime? DueAt { get; init; }
    public bool DueHasTime { get; init; }
    /// <summary>期限からの相対通知（分）。指定すると RemindAt はリポジトリが算出する。</summary>
    public int? RemindOffsetMinutes { get; init; }
    /// <summary>絶対指定の通知日時（UTC）。RemindOffsetMinutes があるときは無視。</summary>
    public DateTime? RemindAt { get; init; }
    public Guid? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public IReadOnlyList<Guid> TagIds { get; init; } = [];
    public IReadOnlyList<string> TagNames { get; init; } = [];
    public Guid? ParentTaskId { get; init; }
    /// <summary>繰り返し。指定すると RecurrenceRule を作って結びつける。</summary>
    public RecurrenceInput? Recurrence { get; init; }
    public int? DurationMinutes { get; init; }
    public double? SortOrder { get; init; }
    public Guid? TemplateBatchId { get; init; }
    /// <summary>あらかじめ決めた Id（テンプレート展開で親子を結ぶため）。null なら新規採番。</summary>
    public Guid? Id { get; init; }
}

/// <summary>繰り返しの設定値。RRule は RFC 5545（例 "FREQ=WEEKLY;BYDAY=MO"）。</summary>
public sealed record RecurrenceInput(
    string RRule,
    RecurrenceBaseKind BaseKind = RecurrenceBaseKind.DueDate,
    RecurrenceEndKind EndKind = RecurrenceEndKind.Never,
    DateOnly? EndDate = null,
    int? MaxOccurrences = null);

/// <summary>サイドバーとトレイの件数。いずれも削除済みを除く未完了（Open）のタスク数。子タスクも1件と数える。</summary>
public sealed record ViewCounts(
    int Today,
    int Overdue,
    int Upcoming,
    int AllOpen,
    int Trash,
    IReadOnlyDictionary<Guid, int> ByProject,
    IReadOnlyDictionary<Guid, int> ByTag)
{
    public static ViewCounts Empty { get; } = new(0, 0, 0, 0, 0, new Dictionary<Guid, int>(), new Dictionary<Guid, int>());
}

/// <summary>振り返りの集計に使う完了タスク1件（Status=Completed のみ、削除済みを除く）。</summary>
public sealed record CompletedTaskFact(
    Guid Id,
    DateTime CompletedAt,
    DateTime CreatedAt,
    DateTime? DueAt,
    bool DueHasTime,
    Guid? ProjectId);

/// <summary>カレンダーの仮表示用: 未完了で繰り返しのある実体タスクと、そのルール。</summary>
public sealed record RecurringTaskInfo(TaskItem Task, RecurrenceRule Rule);

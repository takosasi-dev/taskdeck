namespace TaskDeck.Core;

/// <summary>タスクの状態。DB には数値で入る（設計書 3.3）。System.Threading.Tasks.TaskStatus と衝突しない名前にしている。</summary>
public enum TaskItemStatus
{
    NotStarted = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3,
}

/// <summary>優先度。一覧では ! の個数（低=1 〜 緊急=4）と色を併用する。</summary>
public enum Priority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4,
}

/// <summary>同期状態（Phase 5 用。今は書き込みのたびに Pending になるだけ）。</summary>
public enum SyncState
{
    Synced = 0,
    Pending = 1,
    Conflict = 2,
}

public enum RecurrenceEndKind
{
    Never = 0,
    UntilDate = 1,
    Count = 2,
}

/// <summary>次回を数える起点。期限日から／完了日から。</summary>
public enum RecurrenceBaseKind
{
    DueDate = 0,
    CompletedDate = 1,
}

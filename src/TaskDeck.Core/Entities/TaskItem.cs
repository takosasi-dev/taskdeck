namespace TaskDeck.Core.Entities;

/// <summary>
/// 同期対象のテーブルが共通で持つ監査列。保存時に Data 側のインタフェースで自動的に埋まる
/// （CreatedAt は追加時、UpdatedAt と SyncState=Pending は追加・更新のたび）。
/// </summary>
public interface ISyncTracked
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
    SyncState SyncState { get; set; }
}

/// <summary>
/// タスク（設計書 3.3）。日時はすべて UTC。
/// 日付のみの期限は「その日のローカル 0:00 を UTC に直した値」を DueAt に入れ、DueHasTime=false にする。
/// </summary>
public sealed class TaskItem : ISyncTracked
{
    public const int TitleMaxLength = 500;
    public const int MaxDepth = 2;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public TaskItemStatus Status { get; set; }
    public Priority Priority { get; set; }

    public DateTime? DueAt { get; set; }
    public bool DueHasTime { get; set; }

    /// <summary>通知日時。RemindOffsetMinutes があるときは期限から算出した値が入る（リポジトリが再計算する）。</summary>
    public DateTime? RemindAt { get; set; }
    /// <summary>期限の何分前に通知するか。日付のみの期限では「既定リマインド時刻」からの相対。</summary>
    public int? RemindOffsetMinutes { get; set; }
    public DateTime? NotifiedAt { get; set; }

    /// <summary>閉じた日時。完了（Status=2）と中止（Status=3）の両方で入る。未完了に戻すと null。</summary>
    public DateTime? CompletedAt { get; set; }

    public Guid? ParentTaskId { get; set; }
    /// <summary>階層の深さ 0〜2（親→子→孫）。</summary>
    public int Depth { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? RecurrenceRuleId { get; set; }
    public Guid? RecurrenceSeriesId { get; set; }
    public int? DurationMinutes { get; set; }
    public Guid? TemplateBatchId { get; set; }
    public double SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;
    public DateTime? RemoteUpdatedAt { get; set; }

    public bool IsOpen => Status is TaskItemStatus.NotStarted or TaskItemStatus.InProgress;
    public bool IsDeleted => DeletedAt is not null;

    /// <summary>取り消し用のスナップショット（全列の複製）。</summary>
    public TaskItem Clone() => (TaskItem)MemberwiseClone();
}

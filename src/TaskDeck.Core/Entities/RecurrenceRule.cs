namespace TaskDeck.Core.Entities;

/// <summary>
/// 繰り返しルール（設計書 3.6）。RRULE は RFC 5545 形式（例 "FREQ=WEEKLY;BYDAY=MO"）で、ローカル時刻で評価する。
/// 終了日は日時ではなくローカルの日付で持つ（その日を含む）。
/// </summary>
public sealed class RecurrenceRule : ISyncTracked
{
    public const int RRuleMaxLength = 500;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string RRule { get; set; } = "";
    /// <summary>系列の起点（UTC）。期限なしのタスクに設定したときは作成日時。</summary>
    public DateTime AnchorAt { get; set; }
    public RecurrenceEndKind EndKind { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? MaxOccurrences { get; set; }
    public int CompletedCount { get; set; }
    public RecurrenceBaseKind BaseKind { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;
    public DateTime? RemoteUpdatedAt { get; set; }

    public RecurrenceRule Clone() => (RecurrenceRule)MemberwiseClone();
}

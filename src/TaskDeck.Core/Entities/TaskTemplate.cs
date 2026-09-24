namespace TaskDeck.Core.Entities;

/// <summary>テンプレート（設計書 3.8）。</summary>
public sealed class TaskTemplate : ISyncTracked
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int AnchorLabelMaxLength = 50;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? IconKey { get; set; }
    public string? ColorHex { get; set; }
    /// <summary>基準日の呼び名（「出発日」）。null なら「基準日」と表示する。</summary>
    public string? AnchorLabel { get; set; }
    public Guid? DefaultProjectId { get; set; }
    public List<Guid> DefaultTagIds { get; set; } = [];
    public int UseCount { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public double SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;
    public DateTime? RemoteUpdatedAt { get; set; }
}

/// <summary>
/// テンプレートの項目。期限は基準日からの相対日数（負=前、0=当日、正=後、null=期限なし）で持ち、
/// 時刻だけは絶対値（DueTime、null なら終日）で持つ。
/// </summary>
public sealed class TaskTemplateItem : ISyncTracked
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TemplateId { get; set; }
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public Priority Priority { get; set; }
    public int? DueOffsetDays { get; set; }
    public TimeOnly? DueTime { get; set; }
    public int? RemindOffsetMinutes { get; set; }
    public int? DurationMinutes { get; set; }
    public Guid? ParentItemId { get; set; }
    public int Depth { get; set; }
    public List<Guid> TagIds { get; set; } = [];
    public double SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;
    public DateTime? RemoteUpdatedAt { get; set; }
}

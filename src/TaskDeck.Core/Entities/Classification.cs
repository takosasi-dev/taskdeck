namespace TaskDeck.Core.Entities;

/// <summary>プロジェクト（設計書 3.4）。1タスクは0か1プロジェクトに属する。</summary>
public sealed class Project : ISyncTracked
{
    public const int NameMaxLength = 100;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    /// <summary>#RRGGBB。パレット色ならライトの値を持ち、ダークでは ProjectPalette で対応色に変える。</summary>
    public string ColorHex { get; set; } = "#0067C0";
    public string? IconKey { get; set; }
    public double SortOrder { get; set; }
    public bool IsArchived { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;
    public DateTime? RemoteUpdatedAt { get; set; }

    public Project Clone() => (Project)MemberwiseClone();
}

/// <summary>タグ（設計書 3.5）。名前は大文字小文字を区別せず一意（削除済みを除く）。</summary>
public sealed class Tag : ISyncTracked
{
    public const int NameMaxLength = 50;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public string ColorHex { get; set; } = "#6E6E6E";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;
    public DateTime? RemoteUpdatedAt { get; set; }

    public Tag Clone() => (Tag)MemberwiseClone();
}

/// <summary>タスクとタグの中間表。解除もソフトデリート（DeletedAt）で表す（同期のため）。</summary>
public sealed class TaskTag : ISyncTracked
{
    public Guid TaskId { get; set; }
    public Guid TagId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Pending;
}

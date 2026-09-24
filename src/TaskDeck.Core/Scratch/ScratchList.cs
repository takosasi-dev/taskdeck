using System.Text.Json.Serialization;

namespace TaskDeck.Core.Scratch;

/// <summary>
/// 使い捨てリスト（依頼者の追加要望。仕様書には無い）。タスクとは別物で、項目はチェックと文字と字下げだけを持つ。
/// 保存は AppState の1件（<see cref="ScratchStore"/>）。一覧・検索・件数・振り返り・通知・エクスポートには出ない。
/// </summary>
public sealed class ScratchList
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = "";

    /// <summary>作った日時（UTC）。</summary>
    public DateTime CreatedAt { get; set; }

    public List<ScratchItem> Items { get; set; } = [];
}

/// <summary>項目。Depth は 0〜2（3段）で、前の行より2段以上深くしない（<see cref="ScratchOutline"/> が守る）。</summary>
public sealed class ScratchItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Text { get; set; } = "";

    public bool IsChecked { get; set; }

    public int Depth { get; set; }

    /// <summary>文字が無い行（数えない・テンプレートにしない）。</summary>
    [JsonIgnore]
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

public enum ScratchChange
{
    Created,
    Renamed,
    Discarded,
}

/// <summary>リストが増えた・名前が変わった・捨てられた（項目の中身の変更では出さない）。</summary>
public sealed class ScratchChangedEventArgs(ScratchChange change, Guid listId) : EventArgs
{
    public ScratchChange Change { get; } = change;

    public Guid ListId { get; } = listId;
}

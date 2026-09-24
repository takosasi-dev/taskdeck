using TaskDeck.Core.Abstractions;

namespace TaskDeck.Core.Services;

/// <summary>取り消しの種類。テキスト編集と一括操作は1段だけ持つ（UI 設計書 13.3）。</summary>
public enum UndoKind
{
    /// <summary>完了・未完了の切替。</summary>
    Toggle,
    /// <summary>削除・復元。</summary>
    Delete,
    /// <summary>テキスト編集（タイトル・メモ）。最新の1件だけ残す。</summary>
    TextEdit,
    /// <summary>一括操作（一括完了・移動・テンプレート展開・繰り越しなど）。最新の1件だけ残す。</summary>
    Bulk,
    /// <summary>その他の単発の変更（期限・優先度など）。</summary>
    Other,
}

/// <summary>取り消し1段ぶん。Label はトーストとメニューに出す文（例「「会議資料」を削除しました」）。</summary>
public sealed record UndoEntry(string Label, ChangeSet Changes, UndoKind Kind);

/// <summary>取り消しの履歴（メモリ上、最大10段）。スレッドをまたいで呼ばれても壊れない。</summary>
public sealed class UndoStack
{
    public const int Capacity = 10;

    private readonly LinkedList<UndoEntry> _entries = new();
    private readonly Lock _gate = new();

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Push(UndoEntry entry)
    {
        if (entry.Changes.IsEmpty)
        {
            return;
        }
        lock (_gate)
        {
            if (entry.Kind is UndoKind.TextEdit or UndoKind.Bulk)
            {
                var node = _entries.First;
                while (node is not null)
                {
                    var next = node.Next;
                    if (node.Value.Kind == entry.Kind)
                    {
                        _entries.Remove(node);
                    }
                    node = next;
                }
            }
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public UndoEntry? Peek()
    {
        lock (_gate)
        {
            return _entries.First?.Value;
        }
    }

    public UndoEntry? Pop()
    {
        UndoEntry? entry;
        lock (_gate)
        {
            entry = _entries.First?.Value;
            if (entry is not null)
            {
                _entries.RemoveFirst();
            }
        }
        if (entry is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return entry;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

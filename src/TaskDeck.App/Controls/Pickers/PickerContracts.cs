using TaskDeck.Core;
using TaskDeck.Core.Abstractions;

namespace TaskDeck.App.Controls.Pickers;

// 入力補助ポップアップ（S-09）の受け渡し。全員触らない（ピッカーの中身は担当が作り、ホスト側はこの形だけに頼る）。
//
// ホスト（詳細ペインなど）の使い方:
//   1. Popup（StaysOpen=False）の中にパネルを置き、開く前にパネルの DP に現在値を入れる
//   2. Picked を受けたら値を保存して Popup を閉じる。Cancelled（Esc）を受けたら何もせず閉じる
// パネルは自分で保存しない（DB に書くのはホスト）。ただしプロジェクトの「新しく作る」だけはパネルが作ってから Picked を出す。

/// <summary>日付の選択。Date=null は「期限を消す」。Time=null は終日。</summary>
public sealed class DatePickedEventArgs(DateOnly? date, TimeOnly? time) : EventArgs
{
    public DateOnly? Date { get; } = date;
    public TimeOnly? Time { get; } = time;
}

public sealed class PriorityPickedEventArgs(Priority priority) : EventArgs
{
    public Priority Priority { get; } = priority;
}

/// <summary>プロジェクトの選択。ProjectId=null は「なし」。</summary>
public sealed class ProjectPickedEventArgs(Guid? projectId) : EventArgs
{
    public Guid? ProjectId { get; } = projectId;
}

/// <summary>繰り返しの選択。Recurrence=null は「なし（解除）」。</summary>
public sealed class RecurrencePickedEventArgs(RecurrenceInput? recurrence) : EventArgs
{
    public RecurrenceInput? Recurrence { get; } = recurrence;
}

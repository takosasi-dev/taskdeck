using System.Diagnostics.CodeAnalysis;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.App.ViewModels;

/// <summary>
/// 一覧の構造の操作（波2-E）: Tab / Shift+Tab の階層の上げ下げ（F-025）・複製（F-016）・サブタスクの追加の依頼と、確認・知らせの窓口。
/// </summary>
public sealed partial class TaskListViewModel
{
    /// <summary>
    /// はい／いいえ／キャンセルの確認を出す（View が MessageBox で差し込む）。
    /// 既定は「はい」（テストや View の無いところで止まらないように）。
    /// </summary>
    public Func<ConfirmRequest, ConfirmChoice> Confirm { get; set; } = _ => ConfirmChoice.Yes;

    /// <summary>画面下に短い知らせを出してほしい（階層を変えられなかった理由など。元に戻すは付けない）。</summary>
    public event EventHandler<string>? NoticeRequested;

    /// <summary>行の「サブタスクを追加」: 詳細ペインを開いてサブタスクの欄にフォーカスを入れてほしい（その行は選んである）。</summary>
    public event EventHandler? SubtaskInputRequested;

    /// <summary>
    /// 複製（Ctrl+D・右クリック、F-016）。サブタスクがあれば含めるかを聞く（はい＝含める／いいえ＝このタスクだけ／キャンセル）。
    /// 複製は元のすぐ後ろにでき、それを選んで見せる。
    /// </summary>
    public async Task DuplicateAsync(TaskRowViewModel row)
    {
        if (row.IsInTrash)
        {
            return;
        }
        var includeSubtasks = false;
        if (row.SubtaskCount > 0)
        {
            var request = new ConfirmRequest(
                "サブタスクも複製しますか",
                $"{DisplayText.Quote(row.Title)}にはサブタスクが {row.SubtaskCount} 件あります。\n\n［はい］サブタスクも含めて複製する\n［いいえ］このタスクだけ複製する");
            switch (Confirm(request))
            {
                case ConfirmChoice.Cancel:
                    return;
                case ConfirmChoice.Yes:
                    includeSubtasks = true;
                    break;
            }
        }
        var result = await _tasks.DuplicateAsync(row.Id, includeSubtasks);
        if (result.Created.FirstOrDefault() is not { } copy)
        {
            return;
        }
        _undo.Record(result, DisplayText.Quote(row.Title) + "を複製しました", UndoKind.Other, showToast: false);
        await ReloadAndSelectAsync(copy.Id);
    }

    /// <summary>行の「サブタスクを追加」: その行だけを選び、詳細ペインのサブタスクの欄を開いてもらう（3階層目・ゴミ箱の中は足せない）。</summary>
    public void RequestSubtaskInput(TaskRowViewModel row)
    {
        if (!CanAddSubtask(row))
        {
            return;
        }
        if (IsMultiSelection || !ReferenceEquals(SelectedItem, row))
        {
            ClearSelection();
            SelectedItem = row;
        }
        SubtaskInputRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>その行の下にサブタスクを足せるか（F-022）。</summary>
    public static bool CanAddSubtask(TaskRowViewModel row) => !row.IsInTrash && row.Task.Depth < TaskItem.MaxDepth;

    /// <summary>読み直して、そのタスクの行（複製・移動した先）を選び、見える位置までスクロールしてもらう。</summary>
    private async Task ReloadAndSelectAsync(Guid id)
    {
        _suppressSelectionEvent = true;
        try
        {
            SelectedItem = null;
        }
        finally
        {
            _suppressSelectionEvent = false;
        }
        _selectionDuringLoad = id;
        _otherSelectionDuringLoad = [];
        await ReloadAsync();
        if (FindRow(id) is { } row)
        {
            ScrollIntoViewRequested?.Invoke(this, row);
        }
    }

    /// <summary>Tab: 選んでいる行を1段下げる＝直前の兄弟の子（末尾）にする。</summary>
    public async Task IndentSelectedAsync()
    {
        if (!TryGetRestructurable(out var row))
        {
            return;
        }
        if (PreviousSibling(row) is not { } parent)
        {
            Notice("上に同じ階層のタスクが無いため、1段下げられません");
            return;
        }
        await MoveUnderAsync(row, parent.Id, null, DisplayText.Quote(row.Title) + "を1段下げました");
    }

    /// <summary>Shift+Tab: 選んでいる行を1段上げる＝親の兄弟にして、親のすぐ後ろに置く。</summary>
    public async Task OutdentSelectedAsync()
    {
        if (!TryGetRestructurable(out var row))
        {
            return;
        }
        if (row.Task.ParentTaskId is not { } parentId)
        {
            Notice("いちばん上の階層なので、これ以上は上げられません");
            return;
        }
        var parentRow = FindRow(parentId);
        if ((parentRow?.Task ?? await _tasks.GetAsync(parentId)) is not { } parent)
        {
            return;
        }
        // 親の次の兄弟との間。次の兄弟が一覧に出ていなければ、親の値から Step の半分だけ後ろ（隣の兄弟と同じ値にしない）
        var next = parentRow is null ? null : NextSibling(parentRow);
        var order = SortOrderMath.Between(parent.SortOrder, next?.Task.SortOrder ?? parent.SortOrder + SortOrderMath.Step);
        await MoveUnderAsync(row, parent.ParentTaskId, order, DisplayText.Quote(row.Title) + "を1段上げました");
    }

    /// <summary>
    /// 親を変える（SetParentAsync）。3階層を超える・自分の子孫の下に入れるなどで失敗したら、理由を知らせて false。
    /// </summary>
    private async Task<bool> MoveUnderAsync(TaskRowViewModel row, Guid? parentId, double? sortOrder, string label)
    {
        var result = await _tasks.SetParentAsync(row.Id, parentId, sortOrder);
        if (!result.Succeeded || result.Value is not { } mutation)
        {
            Notice(result.Error ?? "移動できませんでした");
            return false;
        }
        _undo.Record(mutation, label, UndoKind.Other, showToast: false);
        return true;
    }

    /// <summary>Tab / Shift+Tab で階層を変えられる状態か（そうでなければ View は Tab を普通のフォーカス移動に使う）。</summary>
    public bool CanRestructureSelection => TryGetRestructurable(out _);

    /// <summary>
    /// 階層を変えられる行か。階層を見せている一覧（子を親の下に並べる一覧）で、1件だけ選んでいるとき。
    /// 検索結果・完了済み・ゴミ箱は平らに並ぶので、どれが兄弟か分からない。
    /// </summary>
    private bool TryGetRestructurable([NotNullWhen(true)] out TaskRowViewModel? row)
    {
        row = SelectedRow;
        return row is { IsInTrash: false } && !IsMultiSelection && BuildQuery().IncludeSubtasks;
    }

    /// <summary>
    /// 一覧で上にある、同じ親の同じ深さの行（前の兄弟）。前の兄弟の子孫は飛ばし、親かグループの見出しに当たったら無し。
    /// </summary>
    private TaskRowViewModel? PreviousSibling(TaskRowViewModel row)
    {
        for (var i = Rows.IndexOf(row) - 1; i >= 0; i--)
        {
            if (Rows[i] is not TaskRowViewModel other || other.Level < row.Level)
            {
                return null;
            }
            if (other.Level == row.Level && other.Task.ParentTaskId == row.Task.ParentTaskId)
            {
                return other;
            }
        }
        return null;
    }

    /// <summary>一覧で下にある、同じ親の同じ深さの行（次の兄弟）。自分の子孫は飛ばす。</summary>
    private TaskRowViewModel? NextSibling(TaskRowViewModel row)
    {
        for (var i = Rows.IndexOf(row) + 1; i < Rows.Count; i++)
        {
            if (Rows[i] is not TaskRowViewModel other || other.Level < row.Level)
            {
                return null;
            }
            if (other.Level == row.Level && other.Task.ParentTaskId == row.Task.ParentTaskId)
            {
                return other;
            }
        }
        return null;
    }

    private void Notice(string message) => NoticeRequested?.Invoke(this, message);
}

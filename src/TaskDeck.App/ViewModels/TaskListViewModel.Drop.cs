using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.App.ViewModels;

/// <summary>行のどこに落とすか: 上端＝その行の前、下端＝その行の後ろ（並び替え）、真ん中＝その行の子にする。</summary>
public enum DropPosition
{
    Before,
    After,
    Into,
}

/// <summary>落とせるときの中身。Hint はゴーストの右端に出す文言（「2 番目へ」「「会議資料」のサブタスクに」）。</summary>
public sealed record DropPlan(DropPosition Position, string Hint);

/// <summary>
/// ドラッグで運ぶもの（DataObject に入れて、落とす先へ渡す）。Rows は運ぶ行（親と一緒に選んだ子は親に付いてくるので入れない）。
/// Hint は落とす先が決めてゴーストの右端に出す文言（UI 設計書 15.5「N 番目へ」）。
/// </summary>
public sealed partial class TaskDragPayload(IReadOnlyList<TaskRowViewModel> rows) : ObservableObject
{
    public IReadOnlyList<TaskRowViewModel> Rows { get; } = rows;

    public IReadOnlyList<Guid> TaskIds => [.. Rows.Select(r => r.Id)];

    /// <summary>ゴーストに出す名前（1件ならタイトル、複数なら件数）。</summary>
    public string Title => Rows.Count == 1 ? Rows[0].Title : string.Create(CultureInfo.InvariantCulture, $"{Rows.Count} 件のタスク");

    [ObservableProperty]
    private string? _hint;
}

/// <summary>
/// ドラッグ＆ドロップ（波2-E、UI 設計書 15.5）: 行の前後に落とすと並び替え（手動順のときだけ）、行の上に落とすとその行の子にする。
/// サイドバーのプロジェクト・タグへ落とすのは MoveToProjectAsync / AddTagAsync（MainViewModel が中継する）。
/// どれも取り消しに積む（2件以上は一括の1段）。
/// </summary>
public sealed partial class TaskListViewModel
{
    /// <summary>いま運んでいるもの（MainWindow のゴーストが見る）。運んでいないときは null。</summary>
    [ObservableProperty]
    private TaskDragPayload? _activeDrag;

    /// <summary>
    /// 行を掴んだ。掴んだ行が複数選択の中なら選んだ行ぜんぶ、そうでなければその行だけを運ぶ。
    /// 運ぶ行は元の位置に薄く残す（IsDragging）。ゴミ箱の行は運べない（null）。
    /// </summary>
    public TaskDragPayload? BeginDrag(TaskRowViewModel pressed)
    {
        if (pressed.IsInTrash)
        {
            return null;
        }
        IReadOnlyList<TaskRowViewModel> rows = IsMultiSelection && _selected.Contains(pressed)
            ? DragRoots(_selected.Where(r => !r.IsInTrash).ToList())
            : [pressed];
        foreach (var row in rows)
        {
            row.IsDragging = true;
        }
        ActiveDrag = new TaskDragPayload(rows);
        return ActiveDrag;
    }

    /// <summary>ドラッグが終わった（落とした・やめた）。</summary>
    public void EndDrag()
    {
        if (ActiveDrag is not { } drag)
        {
            return;
        }
        foreach (var row in drag.Rows)
        {
            row.IsDragging = false;
        }
        ActiveDrag = null;
    }

    /// <summary>
    /// target の position に落とせるか。落とせなければ null（自分自身・自分の子孫・ゴミ箱の行・手動順でない一覧の並び替え）。
    /// </summary>
    public DropPlan? PlanDrop(TaskDragPayload payload, TaskRowViewModel target, DropPosition position)
    {
        if (target.IsInTrash || payload.Rows.Contains(target) || IsUnderDragged(target, payload))
        {
            return null;
        }
        if (position == DropPosition.Into)
        {
            return new DropPlan(position, DisplayText.Quote(target.Title) + "のサブタスクに");
        }
        if (!IsManualSort)
        {
            return null;
        }
        var siblings = Siblings(target, payload);
        var index = siblings.IndexOf(target) + (position == DropPosition.After ? 1 : 0) + 1;
        return new DropPlan(position, string.Create(CultureInfo.InvariantCulture, $"{index} 番目へ"));
    }

    /// <summary>
    /// 落とした。並び替えは target の兄弟の並び（SortOrder）の間に入れ（SortOrderMath.Between → ReorderAsync。親が違えば SetParentAsync）、
    /// 子にするのは SetParentAsync（3階層を超える・循環なら理由を知らせて止める）。取り消しは1段にまとめる。
    /// </summary>
    public async Task DropAsync(TaskDragPayload payload, TaskRowViewModel target, DropPosition position)
    {
        if (PlanDrop(payload, target, position) is null)
        {
            return;
        }
        var rows = payload.Rows;
        var results = new List<TaskMutationResult>(rows.Count);
        string label;
        if (position == DropPosition.Into)
        {
            foreach (var row in rows)
            {
                var result = await _tasks.SetParentAsync(row.Id, target.Id, null);
                if (!result.Succeeded || result.Value is not { } mutation)
                {
                    Notice(result.Error ?? "移動できませんでした");
                    break;
                }
                results.Add(mutation);
            }
            label = Subject(rows) + "を" + DisplayText.Quote(target.Title) + "のサブタスクにしました";
        }
        else
        {
            var parentId = target.Task.ParentTaskId;
            var siblings = Siblings(target, payload);
            var at = siblings.IndexOf(target);
            double? lower = position == DropPosition.Before ? (at > 0 ? siblings[at - 1].Task.SortOrder : null) : target.Task.SortOrder;
            double? upper = position == DropPosition.Before ? target.Task.SortOrder : (at + 1 < siblings.Count ? siblings[at + 1].Task.SortOrder : null);
            foreach (var row in rows)
            {
                var order = SortOrderMath.Between(lower, upper);
                if (row.Task.ParentTaskId == parentId)
                {
                    results.Add(await _tasks.ReorderAsync(row.Id, order));
                }
                else
                {
                    var result = await _tasks.SetParentAsync(row.Id, parentId, order);
                    if (!result.Succeeded || result.Value is not { } mutation)
                    {
                        Notice(result.Error ?? "移動できませんでした");
                        break;
                    }
                    results.Add(mutation);
                }
                // 運ぶ行が複数なら、前の行のすぐ後ろへ順に入れる（並びを保つ）
                lower = order;
            }
            label = Subject(rows) + "の並びを変えました";
        }
        if (results.Count > 0)
        {
            _undo.Record(Merge(results), label, rows.Count > 1 ? UndoKind.Bulk : UndoKind.Other, showToast: rows.Count > 1);
        }
    }

    /// <summary>
    /// 何回かの書き込みを取り消し1段にまとめる（ApplyUndoAsync は各タスクを最初の変更前へ戻せばよい）。
    /// 同じタスクの変更前は最初のものだけ残し、この中で作ったタスクの変更前は捨てる（作ったものは消すので）。
    /// </summary>
    internal static TaskMutationResult Merge(IReadOnlyList<TaskMutationResult> results)
    {
        if (results.Count == 1)
        {
            return results[0];
        }
        var created = new HashSet<Guid>();
        var createdIds = new List<Guid>();
        var seen = new HashSet<Guid>();
        var before = new List<TaskSnapshot>();
        var ruleIds = new HashSet<Guid>();
        var rules = new List<RecurrenceRule>();
        foreach (var result in results)
        {
            foreach (var snapshot in result.Changes.TasksBefore)
            {
                if (!created.Contains(snapshot.Task.Id) && seen.Add(snapshot.Task.Id))
                {
                    before.Add(snapshot);
                }
            }
            foreach (var id in result.Changes.CreatedTaskIds)
            {
                if (created.Add(id))
                {
                    createdIds.Add(id);
                }
            }
            foreach (var rule in result.Changes.RulesBefore)
            {
                if (ruleIds.Add(rule.Id))
                {
                    rules.Add(rule);
                }
            }
        }
        return new TaskMutationResult(
            new ChangeSet(before, createdIds, rules),
            [.. results.SelectMany(r => r.Affected)],
            [.. results.SelectMany(r => r.Created)]);
    }

    /// <summary>運ぶ行から、同じく運ぶ行の子孫を除く（親を運べば子は付いてくる）。</summary>
    private List<TaskRowViewModel> DragRoots(IReadOnlyList<TaskRowViewModel> rows)
    {
        var ids = rows.Select(r => r.Id).ToHashSet();
        return [.. rows.Where(r => !AncestorIds(r).Any(ids.Contains))];
    }

    /// <summary>target が運ぶ行の子孫か（自分の子の中へは入れられない）。一覧に出ている親だけをたどる（出ていなければリポジトリが断る）。</summary>
    private bool IsUnderDragged(TaskRowViewModel target, TaskDragPayload payload)
    {
        var ids = payload.Rows.Select(r => r.Id).ToHashSet();
        return AncestorIds(target).Any(ids.Contains);
    }

    /// <summary>一覧に出ている祖先の Id（親→祖父。最大2段）。</summary>
    private IEnumerable<Guid> AncestorIds(TaskRowViewModel row)
    {
        var parentId = row.Task.ParentTaskId;
        for (var depth = 0; parentId is { } id && depth < TaskItem.MaxDepth; depth++)
        {
            yield return id;
            parentId = FindRow(id)?.Task.ParentTaskId;
        }
    }

    /// <summary>target の兄弟（同じ親の行。運ぶ行は除く）を並び順（SortOrder）に。見出しでまとめて並べ替えた一覧でも並びの値で決める。</summary>
    private List<TaskRowViewModel> Siblings(TaskRowViewModel target, TaskDragPayload payload) =>
    [
        .. Rows.OfType<TaskRowViewModel>()
            .Where(r => r.Task.ParentTaskId == target.Task.ParentTaskId && (ReferenceEquals(r, target) || !payload.Rows.Contains(r)))
            .OrderBy(r => r.Task.SortOrder)
            .ThenBy(r => r.Id),
    ];

    /// <summary>文の主語（1件ならタイトル、それ以外は件数）。</summary>
    private static string Subject(IReadOnlyList<TaskRowViewModel> rows) =>
        rows.Count == 1 ? DisplayText.Quote(rows[0].Title) : Count(rows.Count);
}

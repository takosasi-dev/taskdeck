using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.ViewModels;

/// <summary>一覧1回ぶんの組み立て結果。Matched は添えた子（IsContext）を除いた件数。</summary>
public sealed record ListBuildResult(IReadOnlyList<ListItemViewModel> Items, int Matched, int MatchedOpen);

/// <summary>
/// 問い合わせの結果を一覧の行に組み立てる（ビューごとのグループ分け。見出しも行として平らに並べる）。
/// 状態を持たないのでスレッドプールで走らせてよい。
/// テンプレートから一度に作った一群（TemplateBatchId）は、同じグループの中に2件以上あれば、最初の1件の位置に見出しを付けてまとめる（F-15B）。
/// 検索結果・完了済み・ゴミ箱ではまとめない（まとめて完了・削除する意味がないため）。
/// </summary>
public static class TaskListBuilder
{
    public static ListBuildResult Build(IReadOnlyList<TreeRow> tree, ViewKind view, RowBuildContext context)
    {
        var blocks = SplitBlocks(tree);
        var items = new List<ListItemViewModel>(tree.Count + 8);
        var matched = 0;
        var matchedOpen = 0;
        foreach (var row in tree)
        {
            if (!row.Row.IsContext)
            {
                matched++;
                if (row.Row.Task.IsOpen)
                {
                    matchedOpen++;
                }
            }
        }

        if (context.IsSearching)
        {
            AddBlocks(items, blocks, context, groupBatches: false);
            return new ListBuildResult(items, matched, matchedOpen);
        }

        var clock = context.Clock;
        var groupBatches = view is not (ViewKind.Completed or ViewKind.Trash);
        switch (view)
        {
            case ViewKind.Today:
                var overdue = blocks.Where(b => TaskRules.IsOverdue(b[0].Row.Task, clock)).ToList();
                var today = blocks.Where(b => !TaskRules.IsOverdue(b[0].Row.Task, clock)).ToList();
                if (overdue.Count > 0)
                {
                    items.Add(new TaskGroupHeaderViewModel("期限切れ", overdue.Count, isWarning: true));
                    AddBlocks(items, overdue, context, groupBatches);
                }
                if (today.Count > 0)
                {
                    items.Add(new TaskGroupHeaderViewModel("今日", today.Count));
                    AddBlocks(items, today, context, groupBatches);
                }
                break;

            case ViewKind.Upcoming:
                AddDateGroups(items, blocks, context, b => b[0].Row.Task.DueAt, groupBatches);
                break;

            case ViewKind.Completed:
                AddDateGroups(items, blocks, context, b => b[0].Row.Task.CompletedAt, groupBatches);
                break;

            default:
                AddBlocks(items, blocks, context, groupBatches);
                break;
        }
        return new ListBuildResult(items, matched, matchedOpen);
    }

    /// <summary>親とその子孫をひとまとまり（ブロック）にする。グループ分けはブロック単位で行う。</summary>
    private static List<IReadOnlyList<TreeRow>> SplitBlocks(IReadOnlyList<TreeRow> tree)
    {
        var blocks = new List<IReadOnlyList<TreeRow>>();
        List<TreeRow>? current = null;
        foreach (var row in tree)
        {
            if (row.Level == 0 || current is null)
            {
                current = [row];
                blocks.Add(current);
            }
            else
            {
                current.Add(row);
            }
        }
        return blocks;
    }

    /// <summary>
    /// ブロックを並べる。groupBatches なら、同じ一群（親の TemplateBatchId）の行が2件以上あるものを、最初のブロックの位置に見出しを付けてまとめる。
    /// </summary>
    private static void AddBlocks(List<ListItemViewModel> items, IReadOnlyList<IReadOnlyList<TreeRow>> blocks, RowBuildContext context, bool groupBatches)
    {
        var batches = groupBatches ? BatchesOf(blocks) : null;
        HashSet<Guid>? emitted = null;
        foreach (var block in blocks)
        {
            if (batches is null || block[0].Row.Task.TemplateBatchId is not { } batchId || !batches.TryGetValue(batchId, out var members))
            {
                AddBlock(items, block, context);
                continue;
            }
            emitted ??= [];
            if (!emitted.Add(batchId))
            {
                continue;
            }
            var created = members.SelectMany(b => b).Where(r => r.Row.Task.TemplateBatchId == batchId).Min(r => r.Row.Task.CreatedAt);
            var name = context.BatchNames.GetValueOrDefault(batchId) ?? "テンプレート";
            items.Add(new TemplateBatchHeaderViewModel(batchId, name, context.Clock.ToLocalDate(created), CountInBatch(members, batchId)));
            foreach (var member in members)
            {
                AddBlock(items, member, context);
            }
        }
    }

    /// <summary>一群（親の TemplateBatchId）ごとのブロック。一群の行（子を含む）が2件以上あるものだけ。無ければ null。</summary>
    private static Dictionary<Guid, List<IReadOnlyList<TreeRow>>>? BatchesOf(IReadOnlyList<IReadOnlyList<TreeRow>> blocks)
    {
        Dictionary<Guid, List<IReadOnlyList<TreeRow>>>? batches = null;
        foreach (var block in blocks)
        {
            if (block[0].Row.Task.TemplateBatchId is not { } batchId)
            {
                continue;
            }
            batches ??= [];
            if (!batches.TryGetValue(batchId, out var list))
            {
                list = [];
                batches[batchId] = list;
            }
            list.Add(block);
        }
        if (batches is null)
        {
            return null;
        }
        foreach (var (batchId, members) in batches.ToList())
        {
            if (CountInBatch(members, batchId) < 2)
            {
                batches.Remove(batchId);
            }
        }
        return batches.Count == 0 ? null : batches;
    }

    private static int CountInBatch(List<IReadOnlyList<TreeRow>> members, Guid batchId) =>
        members.Sum(block => block.Count(r => r.Row.Task.TemplateBatchId == batchId));

    private static void AddBlock(List<ListItemViewModel> items, IReadOnlyList<TreeRow> block, RowBuildContext context)
    {
        foreach (var row in block)
        {
            items.Add(TaskRowViewModel.Create(row.Row, row.Level, context));
        }
    }

    /// <summary>日付ごとの見出し（並びは問い合わせの並びのまま。日付が変わるたびに見出しを入れる）。</summary>
    private static void AddDateGroups(
        List<ListItemViewModel> items,
        IReadOnlyList<IReadOnlyList<TreeRow>> blocks,
        RowBuildContext context,
        Func<IReadOnlyList<TreeRow>, DateTime?> dateOf,
        bool groupBatches)
    {
        var clock = context.Clock;
        var today = clock.LocalToday();
        DateOnly? currentDay = null;
        var pending = new List<IReadOnlyList<TreeRow>>();
        foreach (var block in blocks)
        {
            var day = dateOf(block) is { } utc ? clock.ToLocalDate(utc) : (DateOnly?)null;
            if (pending.Count > 0 && day != currentDay)
            {
                Flush();
            }
            currentDay = day;
            pending.Add(block);
        }
        Flush();

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }
            var title = currentDay is { } d ? DisplayText.DayHeader(d, today) : "期限なし";
            items.Add(new TaskGroupHeaderViewModel(title, pending.Count));
            AddBlocks(items, pending, context, groupBatches);
            pending.Clear();
        }
    }
}

using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;

namespace TaskDeck.App.ViewModels;

/// <summary>3択の確認の答え（はい／いいえ／キャンセル）。</summary>
public enum ConfirmChoice
{
    Cancel,
    No,
    Yes,
}

/// <summary>View に出してもらう確認（MessageBox の はい／いいえ／キャンセル）。Message に［はい］［いいえ］で何が起きるかを書く。</summary>
public sealed record ConfirmRequest(string Title, string Message);

/// <summary>未完了のサブタスクがある親を完了にするときの確認（F-024）。一覧と詳細ペインの両方から使う。</summary>
internal static class SubtaskCompletion
{
    /// <summary>
    /// 完了にするタスクの Id。parents（未完了の子がいるタスク）の子孫に未完了があれば確認し、
    /// はい＝子孫もまとめて・いいえ＝ids だけ・キャンセル＝null。未完了の子孫が無ければ確認せずに ids を返す。
    /// ponytail: 子孫は親ごとに GetDetailAsync で1段ずつ引く（3階層なので最大2段）。何千件もの親を一度に完了にすると遅くなる。
    /// </summary>
    public static async Task<IReadOnlyList<Guid>?> ResolveAsync(
        ITaskRepository tasks,
        IReadOnlyList<Guid> ids,
        IEnumerable<Guid> parents,
        string subject,
        Func<ConfirmRequest, ConfirmChoice> confirm)
    {
        var seen = ids.ToHashSet();
        var open = new List<Guid>();
        foreach (var parentId in parents)
        {
            await CollectOpenAsync(tasks, parentId, level: 1, seen, open);
        }
        if (open.Count == 0)
        {
            return ids;
        }
        var target = ids.Count == 1 ? "このタスク" : "選んだタスク";
        var request = new ConfirmRequest(
            "サブタスクも完了にしますか",
            $"{subject}には未完了のサブタスクが {open.Count} 件あります。\n\n［はい］サブタスクもまとめて完了にする\n［いいえ］{target}だけ完了にする");
        return confirm(request) switch
        {
            ConfirmChoice.Yes => [.. ids, .. open],
            ConfirmChoice.No => ids,
            _ => null,
        };
    }

    private static async Task CollectOpenAsync(ITaskRepository tasks, Guid parentId, int level, HashSet<Guid> seen, List<Guid> open)
    {
        if (await tasks.GetDetailAsync(parentId) is not { } detail)
        {
            return;
        }
        foreach (var child in detail.Children)
        {
            if (child.IsOpen && seen.Add(child.Id))
            {
                open.Add(child.Id);
            }
            if (level < TaskItem.MaxDepth)
            {
                await CollectOpenAsync(tasks, child.Id, level + 1, seen, open);
            }
        }
    }
}

using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.Core.Scratch;

/// <summary>テンプレートの項目と使い捨てリストの項目の行き来（題名と階層だけ。期限・優先度・タグは持ち込まない）。</summary>
public static class ScratchTemplates
{
    /// <summary>
    /// テンプレートの項目を木の順（親の直後に子、兄弟は SortOrder 順）に並べて、題名と深さだけの項目にする。
    /// 親が一覧に無い項目はいちばん上の段に置く。3段より深い項目は消さずに3段目に並べる。
    /// </summary>
    public static List<ScratchItem> FromTemplate(IReadOnlyList<TaskTemplateItem> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        var children = items.ToLookup(i => i.ParentItemId is { } p && p != i.Id && ids.Contains(p) ? p : Guid.Empty);
        var result = new List<ScratchItem>(items.Count);
        Visit(Guid.Empty, 0);
        return result;

        void Visit(Guid parentId, int level)
        {
            foreach (var item in children[parentId].OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
            {
                result.Add(new ScratchItem { Text = item.Title, Depth = Math.Min(level, ScratchOutline.MaxDepth) });
                Visit(item.Id, level + 1);
            }
        }
    }

    /// <summary>
    /// 使い捨てリストの項目からテンプレートの項目を作る（Title・ParentItemId・Depth・SortOrder だけ入れる）。
    /// 文字の無い行は入れない。親は「直前にある1段浅い行」。
    /// </summary>
    public static List<TaskTemplateItem> ToTemplateItems(IReadOnlyList<ScratchItem> items)
    {
        var result = new List<TaskTemplateItem>(items.Count);
        var parents = new TaskTemplateItem[ScratchOutline.MaxDepth + 1];
        var previous = -1;
        foreach (var item in items)
        {
            var title = TextNormalizer.ForTitle(item.Text, TaskItem.TitleMaxLength);
            if (title.Length == 0)
            {
                continue;
            }
            // 空の行を飛ばしたぶん、前の行より2段以上深くなることがあるので詰める
            var depth = Math.Clamp(item.Depth, 0, Math.Min(previous + 1, ScratchOutline.MaxDepth));
            var templateItem = new TaskTemplateItem
            {
                Title = title,
                Depth = depth,
                ParentItemId = depth > 0 ? parents[depth - 1].Id : null,
                SortOrder = (result.Count + 1) * SortOrderMath.Step,
            };
            parents[depth] = templateItem;
            result.Add(templateItem);
            previous = depth;
        }
        return result;
    }
}

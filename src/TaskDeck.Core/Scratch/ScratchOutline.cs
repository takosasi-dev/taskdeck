using TaskDeck.Core.Entities;

namespace TaskDeck.Core.Scratch;

/// <summary>
/// 使い捨てリストの字下げの規則。3段まで（Depth 0〜2）、前の行より2段以上深くしない。
/// 字下げ・字上げ・行の削除は、その行のすぐ後に続く子（より深い行）もまとめて動かす。行の並びは変えない。
/// </summary>
public static class ScratchOutline
{
    /// <summary>いちばん深い段（0 始まり。タスクのサブタスクと同じ3段）。</summary>
    public const int MaxDepth = TaskItem.MaxDepth;

    /// <summary>index の行と、その子（すぐ後に続く、より深い行）の終わり（その次の添字）。</summary>
    public static int SubtreeEnd(IList<ScratchItem> items, int index)
    {
        var end = index + 1;
        while (end < items.Count && items[end].Depth > items[index].Depth)
        {
            end++;
        }
        return end;
    }

    /// <summary>1段下げられるか（前の行より深くならず、子を含めて3段に収まる）。</summary>
    public static bool CanIndent(IList<ScratchItem> items, int index)
    {
        if (index <= 0 || index >= items.Count || items[index].Depth > items[index - 1].Depth)
        {
            return false;
        }
        var end = SubtreeEnd(items, index);
        for (var i = index; i < end; i++)
        {
            if (items[i].Depth >= MaxDepth)
            {
                return false;
            }
        }
        return true;
    }

    public static bool CanOutdent(IList<ScratchItem> items, int index) =>
        index >= 0 && index < items.Count && items[index].Depth > 0;

    /// <summary>子ごと1段下げる（Tab）。できなければ何もしないで false。</summary>
    public static bool Indent(IList<ScratchItem> items, int index) => Shift(items, index, CanIndent(items, index), +1);

    /// <summary>子ごと1段上げる（Shift+Tab）。できなければ何もしないで false。</summary>
    public static bool Outdent(IList<ScratchItem> items, int index) => Shift(items, index, CanOutdent(items, index), -1);

    /// <summary>すぐ下に空の行を足す（Enter）。子があれば最初の子として、無ければ同じ深さで。</summary>
    public static ScratchItem InsertBelow(IList<ScratchItem> items, int index)
    {
        var depth = items[index].Depth;
        if (index + 1 < items.Count && items[index + 1].Depth > depth)
        {
            depth++;
        }
        var item = new ScratchItem { Depth = depth };
        items.Insert(index + 1, item);
        return item;
    }

    /// <summary>行を消す。子は1段上がって、消した行の場所に入る（孫が宙に浮かないように）。</summary>
    public static void Remove(IList<ScratchItem> items, int index)
    {
        var end = SubtreeEnd(items, index);
        for (var i = index + 1; i < end; i++)
        {
            items[i].Depth--;
        }
        items.RemoveAt(index);
    }

    /// <summary>深さを 0〜2 かつ「前の行＋1」までに収める（読み込んだ JSON・作るときの項目に使う）。</summary>
    public static void Normalize(IList<ScratchItem> items)
    {
        var previous = -1;
        foreach (var item in items)
        {
            item.Depth = Math.Clamp(item.Depth, 0, Math.Min(previous + 1, MaxDepth));
            previous = item.Depth;
        }
    }

    private static bool Shift(IList<ScratchItem> items, int index, bool allowed, int delta)
    {
        if (!allowed)
        {
            return false;
        }
        var end = SubtreeEnd(items, index);
        for (var i = index; i < end; i++)
        {
            items[i].Depth += delta;
        }
        return true;
    }
}

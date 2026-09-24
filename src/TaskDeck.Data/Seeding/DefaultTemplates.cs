using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.Data.Seeding;

/// <summary>
/// 初回に入れる3件のテンプレート（F-15C、設計書 4.5）。
/// 多く入れると邪魔になるので、使われ方の型を示す最小限（すべて当日／期限なし＋サブタスク／前後に散るオフセット）に留める。
/// </summary>
internal static class DefaultTemplates
{
    public static IReadOnlyList<(TaskTemplate Template, IReadOnlyList<TaskTemplateItem> Items)> Build() =>
    [
        WeeklyReview(),
        Shopping(),
        NewIssue(),
    ];

    private static (TaskTemplate, IReadOnlyList<TaskTemplateItem>) WeeklyReview()
    {
        var template = New("週次レビュー", "対象の週", "IconCalendar", "#0E7C5A", 1, "その週の終わりに、決まった順でひと通り見直します。");
        return (template, Items(template,
            ("先週の完了タスクを振り返る", 0),
            ("期限切れのタスクを整理する", 0),
            ("今週の予定を確認する", 0),
            ("今週やることを3つ決める", 0),
            ("受信箱とメモを片づける", 0)));
    }

    private static (TaskTemplate, IReadOnlyList<TaskTemplateItem>) Shopping()
    {
        var template = New("買い物リスト", "買い物の日", "IconList", "#CA5010", 2, "行き先ごとに買うものを並べます。期限は付けません。");
        var items = new List<TaskTemplateItem>();
        var food = Item(template, "食料品", null, null, 0, 1 * SortOrderMath.Step);
        items.Add(food);
        var daily = Item(template, "日用品", null, null, 0, 2 * SortOrderMath.Step);
        items.Add(daily);
        items.Add(Item(template, "牛乳", null, food.Id, 1, 1 * SortOrderMath.Step));
        items.Add(Item(template, "卵", null, food.Id, 1, 2 * SortOrderMath.Step));
        items.Add(Item(template, "パン", null, food.Id, 1, 3 * SortOrderMath.Step));
        items.Add(Item(template, "ティッシュペーパー", null, daily.Id, 1, 1 * SortOrderMath.Step));
        return (template, items);
    }

    private static (TaskTemplate, IReadOnlyList<TaskTemplateItem>) NewIssue()
    {
        var template = New("新しい課題に着手", "着手日", "IconPlus", "#8764B8", 3, "着手日を決めると、下調べから振り返りまでの期限が埋まります。");
        return (template, Items(template,
            ("資料と前提を集める", -1),
            ("作業の範囲と完了条件を決める", 0),
            ("途中経過を共有する", 3),
            ("振り返って次の一手を決める", 7)));
    }

    private static TaskTemplate New(string name, string anchorLabel, string iconKey, string colorHex, int order, string description) => new()
    {
        Name = name,
        Description = description,
        AnchorLabel = anchorLabel,
        IconKey = iconKey,
        ColorHex = colorHex,
        SortOrder = order * SortOrderMath.Step,
    };

    private static IReadOnlyList<TaskTemplateItem> Items(TaskTemplate template, params (string Title, int OffsetDays)[] rows) =>
    [
        .. rows.Select((row, i) => Item(template, row.Title, row.OffsetDays, null, 0, (i + 1) * SortOrderMath.Step)),
    ];

    private static TaskTemplateItem Item(TaskTemplate template, string title, int? offsetDays, Guid? parentItemId, int depth, double sortOrder) => new()
    {
        TemplateId = template.Id,
        Title = title,
        DueOffsetDays = offsetDays,
        ParentItemId = parentItemId,
        Depth = depth,
        SortOrder = sortOrder,
    };
}

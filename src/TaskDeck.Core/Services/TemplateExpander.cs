using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Time;

namespace TaskDeck.Core.Services;

/// <summary>展開のしかた（テンプレート画面の入力）。</summary>
public sealed record ExpansionOptions
{
    /// <summary>基準日（ローカル）。</summary>
    public required DateOnly AnchorDate { get; init; }
    /// <summary>チェックを外した項目。親を外すと子孫も作らない。</summary>
    public IReadOnlySet<Guid> ExcludedItemIds { get; init; } = new HashSet<Guid>();
    /// <summary>まとめて入れる先。null ならテンプレートの既定、それも無ければ項目なし。</summary>
    public Guid? ProjectOverride { get; init; }
    /// <summary>付けるタグ。null ならテンプレートの既定＋項目のタグ。指定すると全項目をこのタグにする。</summary>
    public IReadOnlyList<Guid>? TagOverride { get; init; }
    /// <summary>過去になる期限を今日に寄せる（既定 ON、UI 設計書 20.5）。</summary>
    public bool PullPastToToday { get; init; } = true;
}

/// <summary>
/// 展開される1件。OriginalDate は基準日＋相対日数、ResolvedDate は「今日に寄せる」を反映した日付。
/// DueAt は ResolvedDate と DueTime から作った UTC（期限なしなら null）。IsPast は OriginalDate が今日より前か。
/// </summary>
public sealed record PlannedTask(
    Guid NewId,
    Guid SourceItemId,
    Guid? ParentNewId,
    int Depth,
    string Title,
    string? Notes,
    Priority Priority,
    int? DueOffsetDays,
    DateOnly? OriginalDate,
    DateOnly? ResolvedDate,
    TimeOnly? DueTime,
    DateTime? DueAt,
    bool DueHasTime,
    bool IsPast,
    int? RemindOffsetMinutes,
    int? DurationMinutes,
    Guid? ProjectId,
    IReadOnlyList<Guid> TagIds,
    double SortOrder);

/// <summary>展開計画。Tasks は親が先（Depth → SortOrder 順）。PastCount は IsPast の件数。</summary>
public sealed record ExpansionPlan(
    Guid TemplateId,
    Guid BatchId,
    DateOnly AnchorDate,
    IReadOnlyList<PlannedTask> Tasks,
    int PastCount)
{
    /// <summary>リポジトリに渡す形へ。全件に TemplateBatchId=BatchId を付ける。</summary>
    public IReadOnlyList<NewTaskRequest> ToRequests() =>
    [
        .. Tasks.Select(t => new NewTaskRequest
        {
            Id = t.NewId,
            ParentTaskId = t.ParentNewId,
            Title = t.Title,
            Notes = t.Notes,
            Priority = t.Priority,
            DueAt = t.DueAt,
            DueHasTime = t.DueHasTime,
            RemindOffsetMinutes = t.RemindOffsetMinutes,
            DurationMinutes = t.DurationMinutes,
            ProjectId = t.ProjectId,
            TagIds = t.TagIds,
            SortOrder = t.SortOrder,
            TemplateBatchId = BatchId,
        }),
    ];
}

/// <summary>
/// テンプレートと基準日から展開計画を作る（設計書 4.5）。DB には触らない（書き込みは ITemplateRepository.ExpandAsync）。
///
/// 決めごと:
/// - 項目は Depth → SortOrder → Id の順に走査し、旧項目 Id → 新タスク Id の対応で親子を写す
/// - 除外した項目の子孫も作らない（親なしのサブタスクを作らない）。親が見つからない項目はルートとして作る（入力を捨てない）
/// - Depth は写した親から数え直す（3階層を超えないようにテンプレート側の値を鵜呑みにしない）
/// - 期限は基準日＋DueOffsetDays（<see cref="DateOnly.AddDays"/> 任せ）。DueTime があればその時刻、無ければ終日
/// - IsPast は<b>元の日付</b>が今日より前か。「今日に寄せる」は ResolvedDate だけを today にする（IsPast は残す）
/// - タグは TagOverride があればそれだけ、無ければテンプレート既定 ∪ 項目のタグ（重複は除く）
/// - SortOrder は項目の値をそのまま使う（テンプレート内の並びを保つ）
/// </summary>
public sealed class TemplateExpander(IClock clock)
{
    public ExpansionPlan Plan(TaskTemplate template, IReadOnlyList<TaskTemplateItem> items, ExpansionOptions options)
    {
        var today = clock.LocalToday();
        var batchId = Guid.CreateVersion7();
        var byId = new Dictionary<Guid, TaskTemplateItem>(items.Count);
        foreach (var item in items)
        {
            byId[item.Id] = item;
        }
        var excluded = ExcludedClosure(items, byId, options.ExcludedItemIds);

        var newIds = new Dictionary<Guid, Guid>();
        var depths = new Dictionary<Guid, int>();
        var tasks = new List<PlannedTask>(items.Count);
        var pastCount = 0;

        foreach (var item in items.OrderBy(i => i.Depth).ThenBy(i => i.SortOrder).ThenBy(i => i.Id))
        {
            if (excluded.Contains(item.Id))
            {
                continue;
            }

            Guid? parentNewId = null;
            var depth = 0;
            if (item.ParentItemId is { } parentId && newIds.TryGetValue(parentId, out var mapped))
            {
                parentNewId = mapped;
                depth = Math.Min(depths[parentId] + 1, TaskItem.MaxDepth);
            }

            var originalDate = item.DueOffsetDays is { } offset ? options.AnchorDate.AddDays(offset) : (DateOnly?)null;
            var isPast = originalDate is { } d && d < today;
            var resolvedDate = isPast && options.PullPastToToday ? today : originalDate;
            var (dueAt, dueHasTime) = ResolveDue(resolvedDate, item.DueTime);
            if (isPast)
            {
                pastCount++;
            }

            var newId = Guid.CreateVersion7();
            newIds[item.Id] = newId;
            depths[item.Id] = depth;
            tasks.Add(new PlannedTask(
                newId,
                item.Id,
                parentNewId,
                depth,
                TextNormalizer.ForTitle(item.Title, TaskItem.TitleMaxLength),
                item.Notes,
                item.Priority,
                item.DueOffsetDays,
                originalDate,
                resolvedDate,
                item.DueTime,
                dueAt,
                dueHasTime,
                isPast,
                item.RemindOffsetMinutes,
                item.DurationMinutes,
                options.ProjectOverride ?? template.DefaultProjectId,
                ResolveTags(template, item, options),
                item.SortOrder));
        }

        return new ExpansionPlan(template.Id, batchId, options.AnchorDate, tasks, pastCount);
    }

    /// <summary>「7日前」「前日」「当日」「翌日」「3日後」（UI 設計書 20.4）。</summary>
    public static string DescribeOffset(int offsetDays) => offsetDays switch
    {
        0 => "当日",
        -1 => "前日",
        1 => "翌日",
        < 0 => $"{-offsetDays}日前",
        _ => $"{offsetDays}日後",
    };

    private (DateTime? DueAt, bool HasTime) ResolveDue(DateOnly? date, TimeOnly? time)
    {
        if (date is not { } day)
        {
            return (null, false);
        }
        return time is { } at ? (clock.LocalToUtc(day, at), true) : (clock.LocalDayStartUtc(day), false);
    }

    private static IReadOnlyList<Guid> ResolveTags(TaskTemplate template, TaskTemplateItem item, ExpansionOptions options)
    {
        if (options.TagOverride is { } over)
        {
            return [.. over.Distinct()];
        }
        return [.. template.DefaultTagIds.Concat(item.TagIds).Distinct()];
    }

    /// <summary>除外された項目と、その子孫。</summary>
    private static HashSet<Guid> ExcludedClosure(
        IReadOnlyList<TaskTemplateItem> items,
        Dictionary<Guid, TaskTemplateItem> byId,
        IReadOnlySet<Guid> excludedIds)
    {
        var excluded = new HashSet<Guid>(excludedIds);
        if (excluded.Count == 0)
        {
            return excluded;
        }
        foreach (var item in items)
        {
            var current = item;
            for (var depth = 0; depth <= items.Count; depth++)
            {
                if (excluded.Contains(current.Id))
                {
                    excluded.Add(item.Id);
                    break;
                }
                if (current.ParentItemId is not { } parentId || !byId.TryGetValue(parentId, out var parent))
                {
                    break;
                }
                current = parent;
            }
        }
        return excluded;
    }
}

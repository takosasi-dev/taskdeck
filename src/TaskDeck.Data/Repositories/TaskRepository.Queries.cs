using Microsoft.EntityFrameworkCore;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Data.Infrastructure;

namespace TaskDeck.Data.Repositories;

public sealed partial class TaskRepository
{
    // Microsoft.Data.Sqlite の非同期メソッドは中身が同期で動く。UI スレッドから await されても画面が止まらないよう、
    // 件数の多くなる読み込みはスレッドプールで走らせる（書き込みと1件の読み込みは数ミリ秒なので呼び出し元のスレッドのまま）
    public Task<IReadOnlyList<TaskListRow>> QueryAsync(TaskQuery query, CancellationToken ct = default) =>
        Task.Run(() => QueryCoreAsync(query, ct), ct);

    public Task<ViewCounts> GetViewCountsAsync(CancellationToken ct = default) =>
        Task.Run(() => GetViewCountsCoreAsync(ct), ct);

    public Task<IReadOnlyList<CompletedTaskFact>> GetCompletedFactsAsync(DateTime? fromUtc, CancellationToken ct = default) =>
        Task.Run(() => GetCompletedFactsCoreAsync(fromUtc, ct), ct);

    public Task<IReadOnlyList<TaskItem>> GetInDueRangeAsync(DateTime fromUtc, DateTime toUtc, bool includeClosed, CancellationToken ct = default) =>
        Task.Run(() => GetInDueRangeCoreAsync(fromUtc, toUtc, includeClosed, ct), ct);

    private async Task<IReadOnlyList<TaskListRow>> QueryCoreAsync(TaskQuery query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var filtered = ApplyFilter(db, query);
        var matched = await ApplySort(filtered, query).AsNoTracking().ToListAsync(ct);
        if (matched.Count == 0)
        {
            return [];
        }

        var all = new List<(TaskItem Task, bool IsContext)>(matched.Count);
        var seen = new HashSet<Guid>(matched.Count);
        foreach (var task in matched)
        {
            seen.Add(task.Id);
            all.Add((task, false));
        }

        // 一覧に出す Id の集合は SQL の中で作る。
        // EF は Contains をパラメータ1個ずつに展開するので、1万件ぶんの Id を渡すと SQL の組み立てだけで1秒かかる
        var visibleIds = filtered.Select(t => t.Id);
        if (query.IncludeSubtasks)
        {
            // 合ったタスクの子孫（最大2段）を、条件に合わなくても添える
            var childQuery = ChildrenOf(db, filtered);
            var children = await Ordered(childQuery).ToListAsync(ct);
            if (children.Count > 0)
            {
                visibleIds = visibleIds.Union(childQuery.Select(t => t.Id));
                var grandchildQuery = ChildrenOf(db, childQuery);
                var grandchildren = await Ordered(grandchildQuery).ToListAsync(ct);
                if (grandchildren.Count > 0)
                {
                    visibleIds = visibleIds.Union(grandchildQuery.Select(t => t.Id));
                }
                foreach (var task in children.Concat(grandchildren))
                {
                    if (seen.Add(task.Id))
                    {
                        all.Add((task, true));
                    }
                }
            }
        }

        var tagRows = await db.TaskTags.AsNoTracking()
            .Where(tt => tt.DeletedAt == null)
            .Join(visibleIds, tt => tt.TaskId, id => id, (tt, _) => new { tt.TaskId, tt.TagId })
            .ToListAsync(ct);
        var tagLookup = tagRows.ToLookup(x => x.TaskId, x => x.TagId);
        var counts = await db.Tasks.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.ParentTaskId != null)
            .Join(visibleIds, t => t.ParentTaskId!.Value, id => id, (t, _) => t)
            .GroupBy(t => t.ParentTaskId!.Value)
            .Select(g => new
            {
                ParentId = g.Key,
                Total = g.Count(),
                Done = g.Count(t => t.Status == TaskItemStatus.Completed || t.Status == TaskItemStatus.Cancelled),
            })
            .ToDictionaryAsync(x => x.ParentId, ct);

        return
        [
            .. all.Select(x =>
            {
                counts.TryGetValue(x.Task.Id, out var c);
                return new TaskListRow(x.Task, [.. tagLookup[x.Task.Id]], c?.Total ?? 0, c?.Done ?? 0, x.IsContext);
            }),
        ];
    }

    /// <summary>parents の生きている直下の子。結合にしておくと、親の側（少ない方）から IX_Task_Parent を引ける。</summary>
    private static IQueryable<TaskItem> ChildrenOf(TaskDeckDbContext db, IQueryable<TaskItem> parents) =>
        db.Tasks.Where(t => t.DeletedAt == null && t.ParentTaskId != null)
            .Join(parents, t => t.ParentTaskId!.Value, p => p.Id, (t, _) => t);

    private static IQueryable<TaskItem> Ordered(IQueryable<TaskItem> tasks) =>
        tasks.AsNoTracking().OrderBy(t => t.ParentTaskId).ThenBy(t => t.SortOrder).ThenBy(t => t.Id);

    public async Task<TaskItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<IReadOnlyList<TaskItem>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = ids.ToList();
        return await db.Tasks.AsNoTracking().Where(t => list.Contains(t.Id)).ToListAsync(ct);
    }

    public async Task<TaskDetail?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return null;
        }
        var tagIds = await db.TaskTags.AsNoTracking()
            .Where(tt => tt.TaskId == id && tt.DeletedAt == null)
            .Select(tt => tt.TagId)
            .ToListAsync(ct);
        var rule = task.RecurrenceRuleId is { } ruleId
            ? await db.RecurrenceRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId && r.DeletedAt == null, ct)
            : null;
        var children = await db.Tasks.AsNoTracking()
            .Where(t => t.ParentTaskId == id && t.DeletedAt == null)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(ct);
        return new TaskDetail(task, tagIds, rule, children);
    }

    private async Task<ViewCounts> GetViewCountsCoreAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var today = clock.LocalToday();
        var todayStart = clock.LocalDayStartUtc(today);
        var tomorrowStart = clock.LocalDayStartUtc(today.AddDays(1));
        var weekEnd = clock.LocalDayStartUtc(today.AddDays(7));

        var open = db.Tasks.AsNoTracking().Where(t => t.DeletedAt == null
            && (t.Status == TaskItemStatus.NotStarted || t.Status == TaskItemStatus.InProgress));

        var todayCount = await open.CountAsync(t => t.DueAt != null && t.DueAt < tomorrowStart, ct);
        var overdue = await open.CountAsync(t => t.DueAt != null
            && ((t.DueHasTime && t.DueAt < now) || (!t.DueHasTime && t.DueAt < todayStart)), ct);
        var upcoming = await open.CountAsync(t => t.DueAt >= todayStart && t.DueAt < weekEnd, ct);
        var allOpen = await open.CountAsync(ct);
        var trash = await db.Tasks.AsNoTracking().CountAsync(t => t.DeletedAt != null, ct);
        var byProject = await open.Where(t => t.ProjectId != null)
            .GroupBy(t => t.ProjectId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var byTag = await (
                from tt in db.TaskTags.AsNoTracking()
                where tt.DeletedAt == null
                join t in open on tt.TaskId equals t.Id
                group tt by tt.TagId into g
                select new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return new ViewCounts(todayCount, overdue, upcoming, allOpen, trash, byProject, byTag);
    }

    private async Task<IReadOnlyList<CompletedTaskFact>> GetCompletedFactsCoreAsync(DateTime? fromUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // 中止（Cancelled）は「終わらせた」ではないので数えない（設計書 4.6）
        var query = db.Tasks.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.Status == TaskItemStatus.Completed && t.CompletedAt != null);
        if (fromUtc is { } from)
        {
            var fromKind = DateTime.SpecifyKind(from, DateTimeKind.Utc);
            query = query.Where(t => t.CompletedAt >= fromKind);
        }
        return await query
            .OrderBy(t => t.CompletedAt).ThenBy(t => t.Id)
            .Select(t => new CompletedTaskFact(t.Id, t.CompletedAt!.Value, t.CreatedAt, t.DueAt, t.DueHasTime, t.ProjectId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TaskItem>> GetDueRemindersAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return await db.Tasks.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.NotifiedAt == null
                && (t.Status == TaskItemStatus.NotStarted || t.Status == TaskItemStatus.InProgress)
                && t.RemindAt != null && t.RemindAt <= now)
            .OrderBy(t => t.RemindAt).ThenBy(t => t.Id)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<TaskItem>> GetInDueRangeCoreAsync(DateTime fromUtc, DateTime toUtc, bool includeClosed, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);
        var query = db.Tasks.AsNoTracking().Where(t => t.DeletedAt == null && t.DueAt >= from && t.DueAt < to);
        if (!includeClosed)
        {
            query = query.Where(t => t.Status == TaskItemStatus.NotStarted || t.Status == TaskItemStatus.InProgress);
        }
        return await query.OrderBy(t => t.DueAt).ThenBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RecurringTaskInfo>> GetOpenRecurringAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await (
                from t in db.Tasks.AsNoTracking()
                where t.DeletedAt == null && t.RecurrenceRuleId != null
                    && (t.Status == TaskItemStatus.NotStarted || t.Status == TaskItemStatus.InProgress)
                join r in db.RecurrenceRules.AsNoTracking() on t.RecurrenceRuleId equals r.Id
                where r.DeletedAt == null
                orderby t.DueAt, t.Id
                select new RecurringTaskInfo(t, r))
            .ToListAsync(ct);
    }

    /// <summary>TaskQuery を SQL の条件に落とす（全件をメモリに載せてから絞らない）。</summary>
    private IQueryable<TaskItem> ApplyFilter(TaskDeckDbContext db, TaskQuery q)
    {
        IQueryable<TaskItem> t = db.Tasks;
        if (q.Statuses.Count > 0)
        {
            var statuses = q.Statuses.ToList();
            t = t.Where(x => x.DeletedAt == null && statuses.Contains(x.Status));
        }
        else
        {
            // 状態は 0=未着手 1=進行中 2=完了 3=中止 の順（設計書 3.3）。範囲で書くと IX_Task_Due をそのまま引ける
            t = q.Status switch
            {
                TaskStatusFilter.Open => t.Where(x => x.DeletedAt == null && x.Status < TaskItemStatus.Completed),
                TaskStatusFilter.Done => t.Where(x => x.DeletedAt == null && x.Status >= TaskItemStatus.Completed),
                TaskStatusFilter.All => t.Where(x => x.DeletedAt == null),
                TaskStatusFilter.Deleted => t.Where(x => x.DeletedAt != null),
                _ => t,
            };
        }

        var now = clock.UtcNow;
        var today = clock.LocalToday();
        var todayStart = clock.LocalDayStartUtc(today);
        switch (q.Due)
        {
            case DueFilter.Overdue:
                t = t.Where(x => x.DueAt != null && ((x.DueHasTime && x.DueAt < now) || (!x.DueHasTime && x.DueAt < todayStart)));
                break;
            case DueFilter.TodayOrOverdue:
                var tomorrowStart = clock.LocalDayStartUtc(today.AddDays(1));
                t = t.Where(x => x.DueAt != null && x.DueAt < tomorrowStart);
                break;
            case DueFilter.Next7Days:
                var weekEnd = clock.LocalDayStartUtc(today.AddDays(7));
                t = t.Where(x => x.DueAt >= todayStart && x.DueAt < weekEnd);
                break;
            case DueFilter.NoDueDate:
                t = t.Where(x => x.DueAt == null);
                break;
            case DueFilter.HasDueDate:
                t = t.Where(x => x.DueAt != null);
                break;
            case DueFilter.Range:
                t = t.Where(x => x.DueAt != null);
                if (q.DueFrom is { } from)
                {
                    var fromUtc = clock.LocalDayStartUtc(from);
                    t = t.Where(x => x.DueAt >= fromUtc);
                }
                if (q.DueTo is { } to)
                {
                    var toUtc = clock.LocalDayStartUtc(to.AddDays(1));
                    t = t.Where(x => x.DueAt < toUtc);
                }
                break;
        }

        if (q.WithoutProject)
        {
            t = t.Where(x => x.ProjectId == null);
        }
        else if (q.ProjectId is { } projectId)
        {
            t = t.Where(x => x.ProjectId == projectId);
        }
        foreach (var tagId in q.TagIds)
        {
            var id = tagId;
            t = t.Where(x => db.TaskTags.Any(tt => tt.TaskId == x.Id && tt.TagId == id && tt.DeletedAt == null));
        }
        if (q.MinPriority is { } minPriority)
        {
            t = t.Where(x => x.Priority >= minPriority);
        }
        if (q.ClosedFrom is { } closedFrom)
        {
            var closedFromUtc = clock.LocalDayStartUtc(closedFrom);
            t = t.Where(x => x.CompletedAt >= closedFromUtc);
        }
        if (q.TemplateBatchId is { } batchId)
        {
            t = t.Where(x => x.TemplateBatchId == batchId);
        }
        if (q.HasSearch)
        {
            foreach (var term in TextNormalizer.ForSearch(q.SearchText).Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var pattern = "%" + TextNormalizer.EscapeLike(term) + "%";
                t = t.Where(x => EF.Functions.Like(EF.Property<string>(x, AuditInterceptor.SearchKeyProperty), pattern, "\\"));
            }
        }
        return t;
    }

    private static IQueryable<TaskItem> ApplySort(IQueryable<TaskItem> t, TaskQuery q)
    {
        var d = q.Descending;
        IOrderedQueryable<TaskItem> o = q.SortKey switch
        {
            TaskSortKey.Due => d
                ? t.OrderBy(x => x.DueAt == null).ThenByDescending(x => x.DueAt)
                : t.OrderBy(x => x.DueAt == null).ThenBy(x => x.DueAt),
            TaskSortKey.Priority => d ? t.OrderByDescending(x => x.Priority) : t.OrderBy(x => x.Priority),
            TaskSortKey.Created => d ? t.OrderByDescending(x => x.CreatedAt) : t.OrderBy(x => x.CreatedAt),
            TaskSortKey.Title => d ? t.OrderByDescending(x => x.Title) : t.OrderBy(x => x.Title),
            TaskSortKey.Completed => d ? t.OrderByDescending(x => x.CompletedAt) : t.OrderBy(x => x.CompletedAt),
            TaskSortKey.Deleted => d ? t.OrderByDescending(x => x.DeletedAt) : t.OrderBy(x => x.DeletedAt),
            _ => d ? t.OrderByDescending(x => x.SortOrder) : t.OrderBy(x => x.SortOrder),
        };
        return o.ThenBy(x => x.SortOrder).ThenBy(x => x.Id);
    }
}

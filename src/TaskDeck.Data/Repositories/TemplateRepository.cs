using Microsoft.EntityFrameworkCore;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;
using TaskDeck.Data.Seeding;

namespace TaskDeck.Data.Repositories;

/// <summary>
/// テンプレート（設計書 3.8 / 4.5）。展開はタスクの追加とテンプレートの使用回数を1トランザクションで書く
/// （追加の本体は TaskRepository.AddCoreAsync を同じ DbContext で呼ぶ）。
/// </summary>
public sealed class TemplateRepository(
    IDbContextFactory<TaskDeckDbContext> factory,
    IClock clock,
    ISettingsStore settings,
    DataChangeHub hub) : ITemplateRepository
{
    public async Task<IReadOnlyList<TemplateSummary>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Templates.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderByDescending(t => t.UseCount).ThenBy(t => t.SortOrder).ThenBy(t => t.Id)
            .Select(t => new TemplateSummary(t, db.TemplateItems.Count(i => i.TemplateId == t.Id && i.DeletedAt == null)))
            .ToListAsync(ct);
    }

    public async Task<TemplateWithItems?> GetWithItemsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var template = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
        if (template is null)
        {
            return null;
        }
        var items = await db.TemplateItems.AsNoTracking()
            .Where(i => i.TemplateId == id && i.DeletedAt == null)
            .OrderBy(i => i.Depth).ThenBy(i => i.SortOrder).ThenBy(i => i.Id)
            .ToListAsync(ct);
        return new TemplateWithItems(template, items);
    }

    public async Task<TaskTemplate> SaveAsync(TaskTemplate template, IReadOnlyList<TaskTemplateItem> items, CancellationToken ct = default)
    {
        var name = TextNormalizer.TruncateGraphemes(TextNormalizer.ForName(template.Name), TaskTemplate.NameMaxLength);
        if (name.Length == 0)
        {
            throw new ArgumentException("テンプレート名が空です。", nameof(template));
        }
        var normalized = NormalizeItems(items);
        var now = clock.UtcNow;

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Templates.FirstOrDefaultAsync(t => t.Id == template.Id, ct);
        if (row is null)
        {
            row = new TaskTemplate
            {
                Id = template.Id,
                SortOrder = template.SortOrder != 0
                    ? template.SortOrder
                    : SortOrderMath.Between(await db.Templates.MaxAsync(t => (double?)t.SortOrder, ct), null),
            };
            db.Templates.Add(row);
        }
        else if (row.DeletedAt is not null)
        {
            throw new InvalidOperationException("削除済みのテンプレートは保存できません。");
        }
        else
        {
            row.SortOrder = template.SortOrder;
        }
        row.Name = name;
        row.Description = Trim(template.Description, TaskTemplate.DescriptionMaxLength);
        row.IconKey = Trim(template.IconKey, 50);
        row.ColorHex = Trim(template.ColorHex, 7);
        row.AnchorLabel = Trim(template.AnchorLabel, TaskTemplate.AnchorLabelMaxLength);
        row.DefaultProjectId = template.DefaultProjectId;
        row.DefaultTagIds = [.. template.DefaultTagIds];

        var existing = await db.TemplateItems.Where(i => i.TemplateId == row.Id).ToDictionaryAsync(i => i.Id, ct);
        foreach (var item in normalized)
        {
            if (existing.Remove(item.Id, out var current))
            {
                CopyItem(item, current);
            }
            else
            {
                item.TemplateId = row.Id;
                db.TemplateItems.Add(item);
            }
        }
        foreach (var removed in existing.Values.Where(i => i.DeletedAt is null))
        {
            removed.DeletedAt = now;   // 消えた項目はソフトデリート（同期のため）
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        hub.Publish(DataChangeKind.Templates);
        return row;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
        if (template is null)
        {
            return;
        }
        var now = clock.UtcNow;
        var items = await db.TemplateItems.Where(i => i.TemplateId == id && i.DeletedAt == null).ToListAsync(ct);
        foreach (var item in items)
        {
            item.DeletedAt = now;
        }
        template.DeletedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        hub.Publish(DataChangeKind.Templates);
    }

    public async Task<TaskMutationResult> ExpandAsync(ExpansionPlan plan, CancellationToken ct = default)
    {
        if (plan.Tasks.Count == 0)
        {
            return TaskMutationResult.None;
        }
        // 項目の並び順はテンプレートの中だけの値なので、ルートは今ある一覧の末尾に置く（子は新しい親の下なのでそのまま）
        var requests = plan.ToRequests()
            .Select(r => r.ParentTaskId is null ? r with { SortOrder = null } : r)
            .ToList();

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var m = new TaskRepository.Mutation(db);
        await TaskRepository.AddCoreAsync(db, m, requests, clock, settings.Current.Notifications.DefaultReminderTime, ct);

        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == plan.TemplateId && t.DeletedAt == null, ct);
        if (template is not null)
        {
            template.UseCount++;
            template.LastUsedAt = clock.UtcNow;
            m.Kinds |= DataChangeKind.Templates;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        hub.Publish(m.Kinds, m.TouchedIds);
        return m.ToResult();
    }

    public async Task<OperationResult<TaskTemplate>> CreateFromTasksAsync(
        string name,
        IReadOnlyList<Guid> rootTaskIds,
        DateOnly anchorDate,
        CancellationToken ct = default)
    {
        var templateName = TextNormalizer.TruncateGraphemes(TextNormalizer.ForName(name), TaskTemplate.NameMaxLength);
        if (templateName.Length == 0)
        {
            return OperationResult<TaskTemplate>.Fail("テンプレート名を入力してください。");
        }
        var ids = rootTaskIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return OperationResult<TaskTemplate>.Fail("テンプレートにするタスクがありません。");
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var selected = await db.Tasks.AsNoTracking()
            .Where(t => ids.Contains(t.Id) && t.DeletedAt == null)
            .ToListAsync(ct);
        if (selected.Count == 0)
        {
            return OperationResult<TaskTemplate>.Fail("テンプレートにするタスクがありません。");
        }

        // 子孫を2段まで集める（タスクは3階層までなので、これで足りる＝超えるぶんは自然に切り捨てられる）
        var children = await LoadChildrenAsync(db, [.. selected.Select(t => t.Id)], ct);
        var grandchildren = await LoadChildrenAsync(db, [.. children.Select(t => t.Id)], ct);
        // 親と子の両方を選ぶと同じタスクが2回出てくるので、Id でまとめる
        var descendants = children.Concat(grandchildren).DistinctBy(t => t.Id).ToList();
        var descendantIds = descendants.Select(t => t.Id).ToHashSet();
        // 選んだ中に、他の選んだタスクの子孫が混ざっていたら根としては扱わない（二重に入れない）
        var roots = selected.Where(t => !descendantIds.Contains(t.Id)).OrderBy(t => ids.IndexOf(t.Id)).ToList();
        if (roots.Count == 0)
        {
            return OperationResult<TaskTemplate>.Fail("テンプレートにするタスクがありません。");
        }

        var allIds = roots.Select(t => t.Id).Concat(descendantIds).ToList();
        var tagRows = await db.TaskTags.AsNoTracking()
            .Where(tt => tt.DeletedAt == null && allIds.Contains(tt.TaskId))
            .Select(tt => new { tt.TaskId, tt.TagId })
            .ToListAsync(ct);
        var tags = tagRows.ToLookup(x => x.TaskId, x => x.TagId);
        var byParent = descendants.ToLookup(t => t.ParentTaskId!.Value);

        var projectIds = roots.Select(t => t.ProjectId).Distinct().ToList();
        var template = new TaskTemplate
        {
            Name = templateName,
            DefaultProjectId = projectIds.Count == 1 ? projectIds[0] : null,
            SortOrder = SortOrderMath.Between(await db.Templates.MaxAsync(t => (double?)t.SortOrder, ct), null),
        };
        db.Templates.Add(template);

        var items = new List<TaskTemplateItem>();
        AddItems(roots, null, 0);
        db.TemplateItems.AddRange(items);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        hub.Publish(DataChangeKind.Templates);
        return OperationResult<TaskTemplate>.Success(template);

        void AddItems(IReadOnlyList<TaskItem> tasks, Guid? parentItemId, int depth)
        {
            var order = 0;
            foreach (var task in tasks)
            {
                var due = task.DueAt;
                var localDue = due is { } d ? clock.ToLocal(d) : (DateTime?)null;
                var item = new TaskTemplateItem
                {
                    TemplateId = template.Id,
                    Title = task.Title,
                    Notes = task.Notes,
                    Priority = task.Priority,
                    DueOffsetDays = localDue is { } local ? DateOnly.FromDateTime(local).DayNumber - anchorDate.DayNumber : null,
                    DueTime = localDue is { } t2 && task.DueHasTime ? new TimeOnly(t2.Hour, t2.Minute) : null,
                    RemindOffsetMinutes = task.RemindOffsetMinutes,
                    DurationMinutes = task.DurationMinutes,
                    ParentItemId = parentItemId,
                    Depth = depth,
                    TagIds = [.. tags[task.Id]],
                    SortOrder = ++order * SortOrderMath.Step,
                };
                items.Add(item);
                if (depth < TaskItem.MaxDepth)
                {
                    AddItems([.. byParent[task.Id].OrderBy(t => t.SortOrder).ThenBy(t => t.Id)], item.Id, depth + 1);
                }
            }
        }
    }

    public async Task<bool> SeedDefaultsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.AppState.AnyAsync(s => s.Key == AppStateKeys.TemplatesSeeded, ct))
        {
            return false;
        }
        foreach (var (template, items) in DefaultTemplates.Build())
        {
            db.Templates.Add(template);
            db.TemplateItems.AddRange(items);
        }
        db.AppState.Add(new AppStateEntry { Key = AppStateKeys.TemplatesSeeded, Value = "1" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        hub.Publish(DataChangeKind.Templates);
        return true;
    }

    private static async Task<List<TaskItem>> LoadChildrenAsync(TaskDeckDbContext db, IReadOnlyList<Guid> parentIds, CancellationToken ct)
    {
        if (parentIds.Count == 0)
        {
            return [];
        }
        return await db.Tasks.AsNoTracking()
            .Where(t => t.ParentTaskId != null && parentIds.Contains(t.ParentTaskId.Value) && t.DeletedAt == null)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 項目のタイトル・親・深さを整えて、Depth → SortOrder 順に並べ直す。
    /// 親が一覧に無い項目はルート扱いにする。タイトルが空・3階層を超える・Id が重複するのは呼び出し側の誤りなので例外。
    /// </summary>
    private static List<TaskTemplateItem> NormalizeItems(IReadOnlyList<TaskTemplateItem> items)
    {
        var byId = new Dictionary<Guid, TaskTemplateItem>();
        foreach (var item in items)
        {
            if (!byId.TryAdd(item.Id, item))
            {
                throw new ArgumentException("テンプレートの項目 Id が重複しています。", nameof(items));
            }
        }

        var result = new List<TaskTemplateItem>(items.Count);
        foreach (var item in items)
        {
            var title = TextNormalizer.ForTitle(item.Title, TaskItem.TitleMaxLength);
            if (title.Length == 0)
            {
                throw new ArgumentException("タイトルが空の項目は保存できません。", nameof(items));
            }
            var parentId = item.ParentItemId is { } pid && pid != item.Id && byId.ContainsKey(pid) ? pid : (Guid?)null;
            var depth = 0;
            var walk = parentId;
            while (walk is { } id && byId.TryGetValue(id, out var parent))
            {
                depth++;
                if (depth > TaskItem.MaxDepth)
                {
                    throw new ArgumentException("テンプレートの項目は3階層までです。", nameof(items));
                }
                walk = parent.ParentItemId == parent.Id ? null : parent.ParentItemId;
            }
            var copy = new TaskTemplateItem { Id = item.Id };
            CopyItem(item, copy);
            copy.Title = title;
            copy.ParentItemId = parentId;
            copy.Depth = depth;
            result.Add(copy);
        }
        // 親を先に作るため Depth 順（ParentItemId の外部キーを満たす）
        return [.. result.OrderBy(i => i.Depth).ThenBy(i => i.SortOrder).ThenBy(i => i.Id)];
    }

    private static void CopyItem(TaskTemplateItem from, TaskTemplateItem to)
    {
        to.Title = from.Title;
        to.Notes = string.IsNullOrWhiteSpace(from.Notes) ? null : from.Notes;
        to.Priority = from.Priority;
        to.DueOffsetDays = from.DueOffsetDays;
        to.DueTime = from.DueTime;
        to.RemindOffsetMinutes = from.RemindOffsetMinutes;
        to.DurationMinutes = from.DurationMinutes;
        to.ParentItemId = from.ParentItemId;
        to.Depth = from.Depth;
        to.TagIds = [.. from.TagIds];
        to.SortOrder = from.SortOrder;
        to.DeletedAt = null;
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return TextNormalizer.TruncateGraphemes(value.Trim(), maxLength);
    }
}

using Microsoft.EntityFrameworkCore;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Repositories;

public sealed partial class TaskRepository
{
    public Task<TaskMutationResult> AddAsync(NewTaskRequest request, CancellationToken ct = default) =>
        AddManyAsync([request], ct);

    public Task<TaskMutationResult> AddManyAsync(IReadOnlyList<NewTaskRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0)
        {
            return Task.FromResult(TaskMutationResult.None);
        }
        return WriteAsync((db, m) => AddCoreAsync(db, m, requests, clock, DefaultReminderTime, ct), ct);
    }

    public Task<TaskMutationResult> UpdateAsync(Guid id, Action<TaskItem> mutate, CancellationToken ct = default) =>
        UpdateManyAsync([id], mutate, ct);

    public Task<TaskMutationResult> UpdateManyAsync(IReadOnlyCollection<Guid> ids, Action<TaskItem> mutate, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult(TaskMutationResult.None);
        }
        var list = ids.ToList();
        return WriteAsync(async (db, m) =>
        {
            var tasks = await db.Tasks.Where(t => list.Contains(t.Id) && t.DeletedAt == null).ToListAsync(ct);
            await m.SnapshotAsync(tasks, ct);
            foreach (var task in tasks)
            {
                var before = task.Clone();
                mutate(task);
                EnsureOnlyEditableChanged(before, task);
                await ApplyInvariantsAsync(db, m, before, task, ct);
                m.Affect(task);
            }
        }, ct);
    }

    public Task<TaskMutationResult> SetCompletedAsync(IReadOnlyCollection<Guid> ids, bool completed, CancellationToken ct = default) =>
        UpdateManyAsync(ids, t =>
        {
            if (completed)
            {
                t.Status = TaskItemStatus.Completed;
            }
            else if (!t.IsOpen)
            {
                t.Status = TaskItemStatus.NotStarted;
            }
        }, ct);

    public Task<TaskMutationResult> SkipOccurrenceAsync(Guid id, CancellationToken ct = default) =>
        WriteAsync(async (db, m) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
            if (task is null || !task.IsOpen || task.RecurrenceRuleId is null)
            {
                return;
            }
            var rule = await db.RecurrenceRules.FirstOrDefaultAsync(r => r.Id == task.RecurrenceRuleId && r.DeletedAt == null, ct);
            if (rule is null || recurrence.NextDueForSkip(task, rule) is not { } nextDue)
            {
                return;   // 次の発生が無ければ（終了条件に達していれば）何もしない
            }
            await m.SnapshotAsync([task], ct);
            var before = task.Clone();
            var newDue = NormalizeDue(clock, nextDue, task.DueHasTime);
            if (task.RemindOffsetMinutes is null && task.RemindAt is { } remind && task.DueAt is { } oldDue && newDue is { } shifted)
            {
                task.RemindAt = remind + (shifted - oldDue);
            }
            task.DueAt = newDue;
            await ApplyInvariantsAsync(db, m, before, task, ct);
            m.Affect(task);
        }, ct);

    public Task<TaskMutationResult> SetTagsAsync(Guid id, IReadOnlyList<Guid> tagIds, CancellationToken ct = default) =>
        WriteAsync(async (db, m) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
            if (task is null)
            {
                return;
            }
            await m.SnapshotAsync([task], ct);
            var live = await LiveTagIdsAsync(db, tagIds, ct);
            var desired = new Dictionary<Guid, IReadOnlyList<Guid>> { [id] = [.. tagIds.Distinct().Where(live.Contains)] };
            await SetTagSetsAsync(db, desired, clock.UtcNow, ct);
            // タグだけの変更でもタスクの更新日時を進める（同期で変更として送るため）
            db.Entry(task).State = EntityState.Modified;
            m.Affect(task);
        }, ct);

    public Task<TaskMutationResult> AddTagsAsync(IReadOnlyCollection<Guid> ids, IReadOnlyList<Guid> tagIds, CancellationToken ct = default)
    {
        if (ids.Count == 0 || tagIds.Count == 0)
        {
            return Task.FromResult(TaskMutationResult.None);
        }
        var list = ids.ToList();
        return WriteAsync(async (db, m) =>
        {
            var tasks = await db.Tasks.Where(t => list.Contains(t.Id) && t.DeletedAt == null).ToListAsync(ct);
            var live = await LiveTagIdsAsync(db, tagIds, ct);
            var adding = tagIds.Distinct().Where(live.Contains).ToList();
            if (tasks.Count == 0 || adding.Count == 0)
            {
                return;
            }
            var active = await ActiveTagIdsAsync(db, [.. tasks.Select(t => t.Id)], ct);
            var desired = new Dictionary<Guid, IReadOnlyList<Guid>>();
            var changed = new List<TaskItem>();
            foreach (var task in tasks)
            {
                var have = active[task.Id].ToHashSet();
                if (adding.All(have.Contains))
                {
                    continue;   // 既に全部付いている
                }
                have.UnionWith(adding);
                desired[task.Id] = [.. have];
                changed.Add(task);
            }
            if (changed.Count == 0)
            {
                return;
            }
            await m.SnapshotAsync(changed, ct);
            await SetTagSetsAsync(db, desired, clock.UtcNow, ct);
            foreach (var task in changed)
            {
                db.Entry(task).State = EntityState.Modified;
                m.Affect(task);
            }
        }, ct);
    }

    public Task<TaskMutationResult> SetRecurrenceAsync(Guid id, RecurrenceInput? recurrence, CancellationToken ct = default)
    {
        if (recurrence is not null && string.IsNullOrWhiteSpace(recurrence.RRule))
        {
            throw new ArgumentException("繰り返しの指定（RRULE）が空です。", nameof(recurrence));
        }
        return WriteAsync(async (db, m) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
            if (task is null)
            {
                return;
            }
            var now = clock.UtcNow;
            var current = task.RecurrenceRuleId is { } ruleId
                ? await db.RecurrenceRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.DeletedAt == null, ct)
                : null;

            if (recurrence is null)
            {
                if (current is null)
                {
                    return;
                }
                await m.SnapshotAsync([task], ct);
                m.SnapshotRule(current);
                current.DeletedAt = now;      // 生成済みのタスクはそのまま残す（F-036）
                task.RecurrenceRuleId = null;
                m.Affect(task);
                return;
            }

            await m.SnapshotAsync([task], ct);
            if (current is null)
            {
                var created = NewRule(recurrence, task.DueAt ?? now);
                db.RecurrenceRules.Add(created);
                m.RuleCreated(created, now);
                task.RecurrenceRuleId = created.Id;
            }
            else
            {
                m.SnapshotRule(current);
                current.RRule = recurrence.RRule;
                current.BaseKind = recurrence.BaseKind;
                current.EndKind = recurrence.EndKind;
                current.EndDate = recurrence.EndDate;
                current.MaxOccurrences = recurrence.MaxOccurrences;
            }
            task.RecurrenceSeriesId ??= task.Id;
            m.Affect(task);
        }, ct);
    }

    public async Task<OperationResult<TaskMutationResult>> SetParentAsync(Guid id, Guid? newParentId, double? sortOrder, CancellationToken ct = default)
    {
        string? error = null;
        var result = await WriteAsync(async (db, m) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
            if (task is null)
            {
                error = "タスクが見つかりません。";
                return;
            }
            if (newParentId == id)
            {
                error = "自分自身の下には移動できません。";
                return;
            }

            TaskItem? parent = null;
            if (newParentId is { } parentId)
            {
                parent = await db.Tasks.FirstOrDefaultAsync(t => t.Id == parentId && t.DeletedAt == null, ct);
                if (parent is null)
                {
                    error = "移動先のタスクが見つかりません。";
                    return;
                }
            }

            var (children, grandchildren) = await LoadLiveDescendantsAsync(db, [id], ct);
            if (parent is not null && (children.Any(t => t.Id == parent.Id) || grandchildren.Any(t => t.Id == parent.Id)))
            {
                error = "自分のサブタスクの下には移動できません。";
                return;
            }
            var height = grandchildren.Count > 0 ? 2 : children.Count > 0 ? 1 : 0;
            var newDepth = parent is null ? 0 : parent.Depth + 1;
            if (newDepth + height > TaskItem.MaxDepth)
            {
                error = "サブタスクは3階層までです。";
                return;
            }
            if (task.ParentTaskId == newParentId && sortOrder is null)
            {
                return;   // 何も変わらない
            }

            var order = sortOrder ?? SortOrderMath.Between(await MaxSortOrderAsync(db, newParentId, ct), null);
            var crowded = sortOrder is not null && await IsCrowdedAsync(db, newParentId, id, order, ct);

            await m.SnapshotAsync([task, .. children, .. grandchildren], ct);
            task.ParentTaskId = newParentId;
            task.Depth = newDepth;
            task.SortOrder = order;
            m.Affect(task);
            foreach (var child in children)
            {
                child.Depth = newDepth + 1;
                m.Affect(child);
            }
            foreach (var grandchild in grandchildren)
            {
                grandchild.Depth = newDepth + 2;
                m.Affect(grandchild);
            }
            if (crowded)
            {
                await RenumberSiblingsAsync(db, m, newParentId, [task], ct);
            }
        }, ct);

        return error is null ? OperationResult<TaskMutationResult>.Success(result) : OperationResult<TaskMutationResult>.Fail(error);
    }

    public Task<TaskMutationResult> ReorderAsync(Guid id, double sortOrder, CancellationToken ct = default) =>
        WriteAsync(async (db, m) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
            if (task is null)
            {
                return;
            }
            var crowded = await IsCrowdedAsync(db, task.ParentTaskId, id, sortOrder, ct);
            await m.SnapshotAsync([task], ct);
            task.SortOrder = sortOrder;
            m.Affect(task);
            if (crowded)
            {
                await RenumberSiblingsAsync(db, m, task.ParentTaskId, [task], ct);
            }
        }, ct);

    public Task<TaskMutationResult> DuplicateAsync(Guid id, bool includeSubtasks, CancellationToken ct = default) =>
        WriteAsync(async (db, m) =>
        {
            var src = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
            if (src is null)
            {
                return;
            }
            var now = clock.UtcNow;
            // 元のすぐ後ろ（元と次の兄弟の中間）に置く
            var next = await db.Tasks
                .Where(t => t.ParentTaskId == src.ParentTaskId && t.DeletedAt == null && t.SortOrder > src.SortOrder)
                .MinAsync(t => (double?)t.SortOrder, ct);
            var copy = CopyOf(src, src.ParentTaskId, src.Depth, SortOrderMath.Between(src.SortOrder, next), keepSchedule: true);
            db.Tasks.Add(copy);
            m.Created(copy);
            m.Affect(copy);
            await CopyTagsAsync(db, src.Id, copy.Id, ct);

            if (src.RecurrenceRuleId is { } ruleId)
            {
                var rule = await db.RecurrenceRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId && r.DeletedAt == null, ct);
                if (rule is not null)
                {
                    // 複製は別の系列にする（元の履歴と混ざらないように）
                    var copiedRule = new RecurrenceRule
                    {
                        RRule = rule.RRule,
                        AnchorAt = copy.DueAt ?? now,
                        BaseKind = rule.BaseKind,
                        EndKind = rule.EndKind,
                        EndDate = rule.EndDate,
                        MaxOccurrences = rule.MaxOccurrences,
                    };
                    db.RecurrenceRules.Add(copiedRule);
                    m.RuleCreated(copiedRule, now);
                    copy.RecurrenceRuleId = copiedRule.Id;
                    copy.RecurrenceSeriesId = copy.Id;
                }
            }
            if (includeSubtasks)
            {
                await CopyChildrenAsync(db, m, src.Id, copy, keepSchedule: true, ct);
            }
            if (await IsCrowdedAsync(db, copy.ParentTaskId, copy.Id, copy.SortOrder, ct))
            {
                await RenumberSiblingsAsync(db, m, copy.ParentTaskId, [copy], ct);
            }
        }, ct);

    public Task<TaskMutationResult> SoftDeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult(TaskMutationResult.None);
        }
        var list = ids.ToList();
        return WriteAsync(async (db, m) =>
        {
            var tasks = await db.Tasks.Where(t => list.Contains(t.Id) && t.DeletedAt == null).ToListAsync(ct);
            if (tasks.Count == 0)
            {
                return;
            }
            await m.SnapshotAsync(tasks, ct);
            // 削除しない子は親なしに昇格し、その子孫の Depth を詰める（設計書 3.9）
            await PromoteChildrenAsync(db, m, [.. tasks.Select(t => t.Id)], ct);
            var now = clock.UtcNow;
            foreach (var task in tasks)
            {
                task.DeletedAt = now;
                m.Affect(task);
            }
        }, ct);
    }

    public Task<TaskMutationResult> RestoreAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult(TaskMutationResult.None);
        }
        var list = ids.ToList();
        return WriteAsync(async (db, m) =>
        {
            var tasks = await db.Tasks.Where(t => list.Contains(t.Id) && t.DeletedAt != null)
                .OrderBy(t => t.Depth)
                .ToListAsync(ct);
            if (tasks.Count == 0)
            {
                return;
            }
            await m.SnapshotAsync(tasks, ct);
            var restoring = tasks.ToDictionary(t => t.Id);
            foreach (var task in tasks)
            {
                task.DeletedAt = null;
                if (task.ParentTaskId is { } parentId)
                {
                    var parent = restoring.GetValueOrDefault(parentId)
                        ?? await db.Tasks.FirstOrDefaultAsync(p => p.Id == parentId && p.DeletedAt == null, ct);
                    if (parent is null || parent.DeletedAt is not null || parent.Depth + 1 > TaskItem.MaxDepth)
                    {
                        task.ParentTaskId = null;
                        task.Depth = 0;
                    }
                    else
                    {
                        task.Depth = parent.Depth + 1;
                    }
                }
                else
                {
                    task.Depth = 0;   // 親が物理削除されて外れたものも、戻すときにルートへそろえる
                }
                m.Affect(task);
            }
        }, ct);
    }

    public async Task<int> PurgeAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return 0;
        }
        var list = ids.ToList();
        await using var db = await factory.CreateDbContextAsync(ct);
        // ゴミ箱のものだけ。TaskTag は外部キーで消え、残った子の親は NULL になる
        var count = await db.Tasks.Where(t => list.Contains(t.Id) && t.DeletedAt != null).ExecuteDeleteAsync(ct);
        if (count > 0)
        {
            Publish(DataChangeKind.Tasks, list);
        }
        return count;
    }

    public async Task<int> PurgeDeletedAsync(DateTime? deletedBeforeUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Tasks.Where(t => t.DeletedAt != null);
        if (deletedBeforeUtc is { } before)
        {
            var cutoff = DateTime.SpecifyKind(before, DateTimeKind.Utc);
            query = query.Where(t => t.DeletedAt < cutoff);
        }
        var count = await query.ExecuteDeleteAsync(ct);
        if (count > 0)
        {
            Publish(DataChangeKind.Tasks, []);
        }
        return count;
    }

    public Task<TaskMutationResult> CarryOverOverdueAsync(CancellationToken ct = default) =>
        WriteAsync(async (db, m) =>
        {
            var now = clock.UtcNow;
            var todayStart = clock.LocalDayStartUtc(clock.LocalToday());
            var overdue = await db.Tasks
                .Where(t => t.DeletedAt == null
                    && (t.Status == TaskItemStatus.NotStarted || t.Status == TaskItemStatus.InProgress)
                    && t.DueAt != null
                    && ((t.DueHasTime && t.DueAt < now) || (!t.DueHasTime && t.DueAt < todayStart)))
                .ToListAsync(ct);
            if (overdue.Count == 0)
            {
                return;
            }
            await m.SnapshotAsync(overdue, ct);
            foreach (var task in overdue)
            {
                var before = task.Clone();
                task.DueAt = todayStart;
                task.DueHasTime = false;
                await ApplyInvariantsAsync(db, m, before, task, ct);
                m.Affect(task);
            }
        }, ct);

    public Task<TaskMutationResult> MarkNotifiedAsync(IReadOnlyCollection<Guid> ids, DateTime notifiedAtUtc, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return Task.FromResult(TaskMutationResult.None);
        }
        var list = ids.ToList();
        var notifiedAt = DateTime.SpecifyKind(notifiedAtUtc, DateTimeKind.Utc);
        return WriteAsync(async (db, m) =>
        {
            // 読んでから書くまでに再通知・日時の変更が入ったタスクは書かない（新しいリマインドを出した扱いにして消さないため）
            var tasks = await db.Tasks
                .Where(t => list.Contains(t.Id) && t.DeletedAt == null
                    && t.NotifiedAt == null && t.RemindAt != null && t.RemindAt <= notifiedAt)
                .ToListAsync(ct);
            if (tasks.Count == 0)
            {
                return;
            }
            await m.SnapshotAsync(tasks, ct);
            foreach (var task in tasks)
            {
                task.NotifiedAt = notifiedAt;
                m.Affect(task);
            }
        }, ct);
    }

    public Task<TaskMutationResult> SnoozeAsync(Guid id, DateTime remindAtUtc, CancellationToken ct = default) =>
        WriteAsync(async (db, m) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
            if (task is null)
            {
                return;
            }
            await m.SnapshotAsync([task], ct);
            task.RemindAt = DateTime.SpecifyKind(remindAtUtc, DateTimeKind.Utc);
            task.RemindOffsetMinutes = null;   // 相対指定のままだと期限から計算し直されてしまう
            task.NotifiedAt = null;
            m.Affect(task);
        }, ct);

    public async Task ApplyUndoAsync(ChangeSet changes, CancellationToken ct = default)
    {
        if (changes.IsEmpty)
        {
            return;
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;

        // 1. この操作で作られたタスクを消す（未同期なら物理削除、同期済みならソフトデリート）
        var createdIds = changes.CreatedTaskIds.ToList();
        if (createdIds.Count > 0)
        {
            var created = await db.Tasks.Where(t => createdIds.Contains(t.Id)).ToListAsync(ct);
            // 後からその下に付いた子は消さずに親なしへ移す
            await PromoteChildrenAsync(db, null, createdIds, ct);
            foreach (var task in created)
            {
                if (task.RemoteUpdatedAt is null)
                {
                    db.Tasks.Remove(task);
                }
                else
                {
                    task.DeletedAt = now;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        // 2. 変更前の状態に書き戻す（物理削除されていれば作り直す）
        var snapshots = changes.TasksBefore.OrderBy(s => s.Task.Depth).ToList();
        var snapshotIds = snapshots.Select(s => s.Task.Id).ToList();
        var current = await db.Tasks.Where(t => snapshotIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var alive = snapshotIds.ToHashSet();
        alive.UnionWith(current.Keys);
        var missingParents = snapshots.Select(s => s.Task.ParentTaskId).OfType<Guid>().Where(p => !alive.Contains(p)).Distinct().ToList();
        var livingParents = missingParents.Count == 0
            ? []
            : (await db.Tasks.Where(t => missingParents.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();

        foreach (var snapshot in snapshots)
        {
            var before = snapshot.Task;
            // 親がゴミ箱から完全に消えていたら、親なしとして戻す（外部キーが通らないため）
            var orphan = before.ParentTaskId is { } parentId && !alive.Contains(parentId) && !livingParents.Contains(parentId);
            if (current.TryGetValue(before.Id, out var task))
            {
                CopyScalars(before, task);
            }
            else
            {
                task = before.Clone();
                db.Tasks.Add(task);
            }
            if (orphan)
            {
                task.ParentTaskId = null;
                task.Depth = 0;
            }
        }
        await db.SaveChangesAsync(ct);
        await SetTagSetsAsync(db, snapshots.ToDictionary(s => s.Task.Id, s => s.TagIds), now, ct);

        // 3. ルール（完了回数・解除・新規作成）を書き戻す
        var ruleIds = changes.RulesBefore.Select(r => r.Id).ToList();
        var rules = ruleIds.Count == 0
            ? []
            : await db.RecurrenceRules.Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        foreach (var before in changes.RulesBefore)
        {
            if (!rules.TryGetValue(before.Id, out var rule))
            {
                db.RecurrenceRules.Add(before.Clone());
                continue;
            }
            rule.RRule = before.RRule;
            rule.AnchorAt = before.AnchorAt;
            rule.EndKind = before.EndKind;
            rule.EndDate = before.EndDate;
            rule.MaxOccurrences = before.MaxOccurrences;
            rule.CompletedCount = before.CompletedCount;
            rule.BaseKind = before.BaseKind;
            // この操作で作ったルールは「無かった状態」＝ソフトデリートへ戻す
            rule.DeletedAt = before.DeletedAt is null ? null : rule.DeletedAt ?? now;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        Publish(DataChangeKind.Tasks, [.. snapshotIds, .. createdIds]);
    }
}

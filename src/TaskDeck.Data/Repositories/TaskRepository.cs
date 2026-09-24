using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Repositories;

/// <summary>
/// ITaskRepository の実装。読み取りは TaskRepository.Queries.cs、書き込みは TaskRepository.Commands.cs。
/// ここには両方で使う部品（トランザクションの枠、スナップショットの収集、不変条件、名前の解決、親子・並び順）を置く。
/// </summary>
public sealed partial class TaskRepository(
    IDbContextFactory<TaskDeckDbContext> factory,
    IClock clock,
    IRecurrenceEngine recurrence,
    ISettingsStore settings,
    DataChangeHub hub,
    ILogger<TaskRepository> logger) : ITaskRepository
{
    /// <summary>
    /// 1回の書き込みで変わったものを集め、最後に ChangeSet と結果を作る。
    /// 同じトランザクションで作ったプロジェクト・タグの名前も覚えておき、二重に作らない。
    /// </summary>
    internal sealed class Mutation(TaskDeckDbContext db)
    {
        private readonly Dictionary<Guid, TaskSnapshot> _before = [];
        private readonly Dictionary<Guid, RecurrenceRule> _rulesBefore = [];
        private readonly List<Guid> _created = [];
        private readonly HashSet<Guid> _createdIds = [];
        private readonly List<TaskItem> _createdTasks = [];
        private readonly Dictionary<Guid, TaskItem> _affected = [];

        public DataChangeKind Kinds { get; set; } = DataChangeKind.Tasks;

        public Dictionary<string, Guid> ProjectNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Guid> TagNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>何も変わっていない（通知も取り消しも要らない）。</summary>
        public bool IsEmpty => _before.Count == 0 && _created.Count == 0 && _rulesBefore.Count == 0;

        /// <summary>変更前の状態を記録する（同じタスクは最初の1回だけ）。追跡中のエンティティを、変える前に渡すこと。</summary>
        public async Task SnapshotAsync(IReadOnlyCollection<TaskItem> tasks, CancellationToken ct)
        {
            var missing = tasks.Where(t => !_before.ContainsKey(t.Id) && !_createdIds.Contains(t.Id)).ToList();
            if (missing.Count == 0)
            {
                return;
            }
            var ids = missing.Select(t => t.Id).ToList();
            var tags = await db.TaskTags.AsNoTracking()
                .Where(tt => ids.Contains(tt.TaskId) && tt.DeletedAt == null)
                .Select(tt => new { tt.TaskId, tt.TagId })
                .ToListAsync(ct);
            var byTask = tags.ToLookup(x => x.TaskId, x => x.TagId);
            foreach (var t in missing)
            {
                _before[t.Id] = new TaskSnapshot(t.Clone(), [.. byTask[t.Id]]);
            }
        }

        public void SnapshotRule(RecurrenceRule rule) => _rulesBefore.TryAdd(rule.Id, rule.Clone());

        /// <summary>新しく作ったルール。取り消しでは「無かった状態」に戻す＝ソフトデリートするので、削除済みの姿を控える。</summary>
        public void RuleCreated(RecurrenceRule rule, DateTime now)
        {
            var snapshot = rule.Clone();
            snapshot.DeletedAt = now;
            _rulesBefore.TryAdd(rule.Id, snapshot);
        }

        public void Affect(TaskItem task) => _affected[task.Id] = task;

        public void Created(TaskItem task)
        {
            _created.Add(task.Id);
            _createdIds.Add(task.Id);
            _createdTasks.Add(task);
        }

        public IReadOnlyCollection<Guid> TouchedIds => [.. _before.Keys, .. _created];

        public TaskMutationResult ToResult() => new(
            new ChangeSet([.. _before.Values], [.. _created], [.. _rulesBefore.Values]),
            [.. _affected.Values.Select(t => t.Clone())],
            [.. _createdTasks.Select(t => t.Clone())]);
    }

    /// <summary>コミット後の通知。購読側の例外で書き込みを失敗扱いにしない（書き込みは済んでいる）。</summary>
    private void Publish(DataChangeKind kinds, IReadOnlyCollection<Guid> ids)
    {
        try
        {
            hub.Publish(kinds, ids);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "変更通知の購読側で例外が起きました");
        }
    }

    /// <summary>書き込みの共通の枠（メソッド1回＝1トランザクション）。変更があったときだけ通知する。</summary>
    private async Task<TaskMutationResult> WriteAsync(Func<TaskDeckDbContext, Mutation, Task> write, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var m = new Mutation(db);
        await write(db, m);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (!m.IsEmpty)
        {
            Publish(m.Kinds, m.TouchedIds);
        }
        return m.ToResult();
    }

    private TimeOnly DefaultReminderTime => settings.Current.Notifications.DefaultReminderTime;

    /// <summary>日付のみの期限はローカル 0:00 の UTC にそろえる。時刻ありは UTC のまま。</summary>
    internal static DateTime? NormalizeDue(IClock clock, DateTime? dueAt, bool hasTime)
    {
        if (dueAt is not { } due)
        {
            return null;
        }
        var utc = DateTime.SpecifyKind(due, DateTimeKind.Utc);
        return hasTime ? utc : clock.LocalDayStartUtc(clock.ToLocalDate(utc));
    }

    // ---- 追加（TemplateRepository.ExpandAsync も同じ DbContext・トランザクションの中でこれを使う）----

    /// <summary>
    /// タスクを追加する本体。requests は親を先に並べること（Id と ParentTaskId で親子を指定できる）。
    /// プロジェクト名・タグ名は同じトランザクションで引き、無ければ作る（F-044）。
    /// </summary>
    internal static async Task AddCoreAsync(
        TaskDeckDbContext db,
        Mutation m,
        IReadOnlyList<NewTaskRequest> requests,
        IClock clock,
        TimeOnly defaultReminderTime,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var batch = new Dictionary<Guid, TaskItem>();
        var lastOrder = new Dictionary<Guid, double>();          // 親 Id（ルートは Guid.Empty）ごとの末尾
        var wanted = new List<(Guid TaskId, Guid TagId)>();      // 付けたいタグ（生きているかは後でまとめて確かめる）
        var trustedTags = new HashSet<Guid>();                   // 名前から作った・引いたタグ（確かめ直さない）

        foreach (var r in requests)
        {
            var title = TextNormalizer.ForTitle(r.Title, TaskItem.TitleMaxLength);
            if (title.Length == 0)
            {
                throw new ArgumentException("タイトルが空のタスクは作れません。", nameof(requests));
            }
            var task = new TaskItem
            {
                Id = r.Id ?? Guid.CreateVersion7(),
                Title = title,
                Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
                Status = r.Status,
                Priority = r.Priority,
                DueHasTime = r.DueAt is not null && r.DueHasTime,
                DueAt = NormalizeDue(clock, r.DueAt, r.DueHasTime),
                RemindOffsetMinutes = r.RemindOffsetMinutes,
                RemindAt = r.RemindOffsetMinutes is null ? r.RemindAt : null,
                DurationMinutes = r.DurationMinutes,
                TemplateBatchId = r.TemplateBatchId,
            };
            task.ProjectId = r.ProjectId ?? await ResolveProjectAsync(db, r.ProjectName, m, ct);

            if (r.ParentTaskId is { } parentId)
            {
                var parent = batch.GetValueOrDefault(parentId)
                    ?? await db.Tasks.FirstOrDefaultAsync(t => t.Id == parentId && t.DeletedAt == null, ct)
                    ?? throw new InvalidOperationException("親タスクが見つかりません。");
                if (parent.Depth >= TaskItem.MaxDepth)
                {
                    throw new InvalidOperationException("サブタスクは3階層までです。");
                }
                task.ParentTaskId = parentId;
                task.Depth = parent.Depth + 1;
            }

            var siblingKey = task.ParentTaskId ?? Guid.Empty;
            if (!lastOrder.TryGetValue(siblingKey, out var last))
            {
                last = await MaxSortOrderAsync(db, task.ParentTaskId, ct);
            }
            task.SortOrder = r.SortOrder ?? SortOrderMath.Between(last, null);
            lastOrder[siblingKey] = Math.Max(task.SortOrder, last);

            if (!task.IsOpen)
            {
                task.CompletedAt = now;
            }
            if (task.RemindOffsetMinutes is not null)
            {
                task.RemindAt = TaskRules.ComputeRemindAt(task.DueAt, task.DueHasTime, task.RemindOffsetMinutes, defaultReminderTime, clock);
            }
            if (r.Recurrence is { } rec)
            {
                var rule = NewRule(rec, task.DueAt ?? now);
                db.RecurrenceRules.Add(rule);
                m.RuleCreated(rule, now);
                task.RecurrenceRuleId = rule.Id;
                task.RecurrenceSeriesId = task.Id;
            }

            db.Tasks.Add(task);
            batch[task.Id] = task;
            m.Created(task);
            m.Affect(task);

            foreach (var tagId in r.TagIds)
            {
                wanted.Add((task.Id, tagId));
            }
            foreach (var name in r.TagNames)
            {
                if (await ResolveTagAsync(db, name, m, ct) is { } tagId)
                {
                    trustedTags.Add(tagId);
                    wanted.Add((task.Id, tagId));
                }
            }
        }

        // 消えたタグを付け直さない（テンプレートの項目が古いタグ Id を持っていることがある）
        var check = wanted.Select(w => w.TagId).Where(id => !trustedTags.Contains(id)).Distinct().ToList();
        var live = await LiveTagIdsAsync(db, check, ct);
        foreach (var (taskId, tagId) in wanted.Distinct())
        {
            if (trustedTags.Contains(tagId) || live.Contains(tagId))
            {
                db.TaskTags.Add(new TaskTag { TaskId = taskId, TagId = tagId });
            }
        }
    }

    private static async Task<Guid?> ResolveProjectAsync(TaskDeckDbContext db, string? name, Mutation m, CancellationToken ct)
    {
        var normalized = ProjectRepository.NormalizeName(name);
        if (normalized.Length == 0)
        {
            return null;
        }
        if (m.ProjectNames.TryGetValue(normalized, out var cached))
        {
            return cached;
        }
        var lower = normalized.ToLowerInvariant();
        var existing = await db.Projects
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == lower)
            .OrderBy(p => p.IsArchived).ThenBy(p => p.SortOrder)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id)
        {
            m.ProjectNames[normalized] = id;
            return id;
        }
        var used = await db.Projects.Where(p => p.DeletedAt == null).Select(p => p.ColorHex).ToListAsync(ct);
        var maxOrder = await db.Projects.Where(p => p.DeletedAt == null).MaxAsync(p => (double?)p.SortOrder, ct);
        var project = new Project
        {
            Name = normalized,
            ColorHex = ProjectPalette.NextColor([.. used, .. db.Projects.Local.Select(p => p.ColorHex)]),
            SortOrder = SortOrderMath.Between(Math.Max(maxOrder ?? 0, db.Projects.Local.Select(p => p.SortOrder).DefaultIfEmpty(0).Max()), null),
        };
        db.Projects.Add(project);
        m.ProjectNames[normalized] = project.Id;
        m.Kinds |= DataChangeKind.Projects;
        return project.Id;
    }

    private static async Task<Guid?> ResolveTagAsync(TaskDeckDbContext db, string? name, Mutation m, CancellationToken ct)
    {
        var normalized = TagRepository.NormalizeName(name);
        if (normalized.Length == 0)
        {
            return null;
        }
        if (m.TagNames.TryGetValue(normalized, out var cached))
        {
            return cached;
        }
        // Name 列は NOCASE なので = で大文字小文字を無視して引ける
        var existing = await db.Tags
            .Where(t => t.DeletedAt == null && t.Name == normalized)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id)
        {
            m.TagNames[normalized] = id;
            return id;
        }
        var tag = new Tag { Name = normalized };
        db.Tags.Add(tag);
        m.TagNames[normalized] = tag.Id;
        m.Kinds |= DataChangeKind.Tags;
        return tag.Id;
    }

    // ---- 不変条件 ----

    /// <summary>
    /// 値の変更後に守る不変条件（ITaskRepository の説明を参照）。before は変更前の複製。
    /// 完了への遷移で繰り返しの次回を作ったら、そのタスクを Mutation に登録する。
    /// </summary>
    private async Task ApplyInvariantsAsync(TaskDeckDbContext db, Mutation m, TaskItem before, TaskItem task, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var title = TextNormalizer.ForTitle(task.Title, TaskItem.TitleMaxLength);
        task.Title = title.Length == 0 ? before.Title : title;
        task.Notes = string.IsNullOrWhiteSpace(task.Notes) ? null : task.Notes;

        task.DueHasTime = task.DueAt is not null && task.DueHasTime;
        task.DueAt = NormalizeDue(clock, task.DueAt, task.DueHasTime);

        var wasOpen = before.IsOpen;
        if (task.IsOpen)
        {
            task.CompletedAt = null;
        }
        else if (wasOpen || task.CompletedAt is null)
        {
            task.CompletedAt = now;
        }

        if (task.RemindOffsetMinutes is not null)
        {
            task.RemindAt = TaskRules.ComputeRemindAt(task.DueAt, task.DueHasTime, task.RemindOffsetMinutes, DefaultReminderTime, clock);
        }
        if (task.RemindAt != before.RemindAt)
        {
            task.NotifiedAt = null;
        }

        if (wasOpen && task.Status == TaskItemStatus.Completed && task.RecurrenceRuleId is not null)
        {
            await CreateNextOccurrenceAsync(db, m, task, now, ct);
        }
    }

    /// <summary>繰り返しの次回を1件作る（設計書 4.1）。同じ系列に未完了が残っていれば作らない。</summary>
    private async Task CreateNextOccurrenceAsync(TaskDeckDbContext db, Mutation m, TaskItem done, DateTime completedAt, CancellationToken ct)
    {
        var rule = await db.RecurrenceRules.FirstOrDefaultAsync(r => r.Id == done.RecurrenceRuleId && r.DeletedAt == null, ct);
        if (rule is null)
        {
            return;
        }
        var seriesId = done.RecurrenceSeriesId ?? done.Id;
        done.RecurrenceSeriesId = seriesId;
        var openSibling = await db.Tasks.AnyAsync(
            t => t.RecurrenceSeriesId == seriesId && t.Id != done.Id && t.DeletedAt == null
                 && (t.Status == TaskItemStatus.NotStarted || t.Status == TaskItemStatus.InProgress),
            ct);
        if (openSibling)
        {
            return;
        }
        var nextDue = recurrence.NextDue(done, rule, completedAt);
        m.SnapshotRule(rule);
        rule.CompletedCount++;
        if (nextDue is null)
        {
            return;
        }

        var next = CopyOf(done, done.ParentTaskId, done.Depth, done.SortOrder, keepSchedule: false);
        next.DueHasTime = done.DueHasTime;
        next.DueAt = NormalizeDue(clock, nextDue, done.DueHasTime);
        next.RemindOffsetMinutes = done.RemindOffsetMinutes;
        next.RecurrenceRuleId = rule.Id;
        next.RecurrenceSeriesId = seriesId;
        if (next.RemindOffsetMinutes is not null)
        {
            next.RemindAt = TaskRules.ComputeRemindAt(next.DueAt, next.DueHasTime, next.RemindOffsetMinutes, DefaultReminderTime, clock);
        }
        else if (done.RemindAt is { } remind && done.DueAt is { } oldDue && next.DueAt is { } newDue)
        {
            next.RemindAt = remind + (newDue - oldDue);
        }
        db.Tasks.Add(next);
        m.Created(next);
        await CopyTagsAsync(db, done.Id, next.Id, ct);
        await CopyChildrenAsync(db, m, done.Id, next, keepSchedule: false, ct);
    }

    /// <summary>複製の元になる値だけを写した新しいタスク（状態は未着手）。keepSchedule=false なら期限・通知を引き継がない。</summary>
    private static TaskItem CopyOf(TaskItem src, Guid? parentId, int depth, double sortOrder, bool keepSchedule) => new()
    {
        Title = src.Title,
        Notes = src.Notes,
        Priority = src.Priority,
        ProjectId = src.ProjectId,
        DurationMinutes = src.DurationMinutes,
        ParentTaskId = parentId,
        Depth = depth,
        SortOrder = sortOrder,
        DueAt = keepSchedule ? src.DueAt : null,
        DueHasTime = keepSchedule && src.DueHasTime,
        RemindAt = keepSchedule ? src.RemindAt : null,
        RemindOffsetMinutes = keepSchedule ? src.RemindOffsetMinutes : null,
        NotifiedAt = keepSchedule ? src.NotifiedAt : null,
    };

    private static async Task CopyTagsAsync(TaskDeckDbContext db, Guid fromTaskId, Guid toTaskId, CancellationToken ct)
    {
        var tagIds = await db.TaskTags.AsNoTracking()
            .Where(tt => tt.TaskId == fromTaskId && tt.DeletedAt == null)
            .Select(tt => tt.TagId)
            .ToListAsync(ct);
        foreach (var tagId in tagIds)
        {
            db.TaskTags.Add(new TaskTag { TaskId = toTaskId, TagId = tagId });
        }
    }

    /// <summary>子孫の構成を未着手で複製する（繰り返しの次回・複製で使う）。</summary>
    private static async Task CopyChildrenAsync(TaskDeckDbContext db, Mutation m, Guid fromParentId, TaskItem toParent, bool keepSchedule, CancellationToken ct)
    {
        var children = await db.Tasks.AsNoTracking()
            .Where(t => t.ParentTaskId == fromParentId && t.DeletedAt == null)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(ct);
        foreach (var child in children)
        {
            var copy = CopyOf(child, toParent.Id, toParent.Depth + 1, child.SortOrder, keepSchedule);
            db.Tasks.Add(copy);
            m.Created(copy);
            await CopyTagsAsync(db, child.Id, copy.Id, ct);
            if (copy.Depth < TaskItem.MaxDepth)
            {
                await CopyChildrenAsync(db, m, child.Id, copy, keepSchedule, ct);
            }
        }
    }

    /// <summary>mutate で変えてはいけない列が変わっていないか（プログラムの誤りなので例外）。</summary>
    private static void EnsureOnlyEditableChanged(TaskItem before, TaskItem after)
    {
        if (before.Id != after.Id
            || before.ParentTaskId != after.ParentTaskId
            || before.Depth != after.Depth
            || before.RecurrenceRuleId != after.RecurrenceRuleId
            || before.RecurrenceSeriesId != after.RecurrenceSeriesId
            || before.TemplateBatchId != after.TemplateBatchId
            || !before.SortOrder.Equals(after.SortOrder)
            || before.CreatedAt != after.CreatedAt
            || before.DeletedAt != after.DeletedAt
            || before.CompletedAt != after.CompletedAt
            || before.NotifiedAt != after.NotifiedAt
            || before.RemoteUpdatedAt != after.RemoteUpdatedAt)
        {
            throw new InvalidOperationException(
                "UpdateAsync で変えられるのは Title, Notes, Status, Priority, DueAt, DueHasTime, RemindAt, RemindOffsetMinutes, ProjectId, DurationMinutes だけです。");
        }
    }

    // ---- タグ ----

    /// <summary>タスクごとの、今付いているタグ。</summary>
    private static async Task<ILookup<Guid, Guid>> ActiveTagIdsAsync(TaskDeckDbContext db, IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0)
        {
            return Array.Empty<(Guid, Guid)>().ToLookup(x => x.Item1, x => x.Item2);
        }
        var ids = taskIds.ToList();
        var rows = await db.TaskTags.AsNoTracking()
            .Where(tt => tt.DeletedAt == null && ids.Contains(tt.TaskId))
            .Select(tt => new { tt.TaskId, tt.TagId })
            .ToListAsync(ct);
        return rows.ToLookup(x => x.TaskId, x => x.TagId);
    }

    /// <summary>タスクのタグを desired に合わせる（外したものはソフトデリート、付け直しは復活、無いものは追加）。</summary>
    private static async Task SetTagSetsAsync(
        TaskDeckDbContext db,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> desired,
        DateTime now,
        CancellationToken ct)
    {
        if (desired.Count == 0)
        {
            return;
        }
        var taskIds = desired.Keys.ToList();
        var rows = await db.TaskTags.Where(x => taskIds.Contains(x.TaskId)).ToListAsync(ct);
        var byTask = rows.ToLookup(x => x.TaskId);
        foreach (var (taskId, tagIds) in desired)
        {
            var want = tagIds.ToHashSet();
            foreach (var row in byTask[taskId])
            {
                if (want.Remove(row.TagId))
                {
                    row.DeletedAt = null;
                }
                else if (row.DeletedAt is null)
                {
                    row.DeletedAt = now;
                }
            }
            foreach (var tagId in want)
            {
                db.TaskTags.Add(new TaskTag { TaskId = taskId, TagId = tagId });
            }
        }
    }

    /// <summary>生きているタグだけに絞る（消えたタグを付け直さない）。</summary>
    private static async Task<HashSet<Guid>> LiveTagIdsAsync(TaskDeckDbContext db, IReadOnlyCollection<Guid> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0)
        {
            return [];
        }
        var ids = tagIds.Distinct().ToList();
        var found = await db.Tags.Where(t => t.DeletedAt == null && ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct);
        return [.. found];
    }

    // ---- 親子・並び順 ----

    /// <summary>生きている子と孫（それぞれ追跡中）。</summary>
    private static async Task<(List<TaskItem> Children, List<TaskItem> Grandchildren)> LoadLiveDescendantsAsync(
        TaskDeckDbContext db,
        IReadOnlyCollection<Guid> parentIds,
        CancellationToken ct)
    {
        var ids = parentIds.ToList();
        if (ids.Count == 0)
        {
            return ([], []);
        }
        var children = await db.Tasks
            .Where(t => t.ParentTaskId != null && ids.Contains(t.ParentTaskId.Value) && t.DeletedAt == null)
            .ToListAsync(ct);
        var childIds = children.Select(t => t.Id).ToList();
        var grandchildren = childIds.Count == 0
            ? []
            : await db.Tasks
                .Where(t => t.ParentTaskId != null && childIds.Contains(t.ParentTaskId.Value) && t.DeletedAt == null)
                .ToListAsync(ct);
        return (children, grandchildren);
    }

    /// <summary>parentIds の下にいる生きている子を親なしに昇格し、その子の Depth を詰める（親を消すとき）。</summary>
    private static async Task PromoteChildrenAsync(TaskDeckDbContext db, Mutation? m, IReadOnlyCollection<Guid> parentIds, CancellationToken ct)
    {
        var (children, grandchildren) = await LoadLiveDescendantsAsync(db, parentIds, ct);
        var gone = parentIds.ToHashSet();
        var promoted = children.Where(t => !gone.Contains(t.Id)).ToList();
        if (promoted.Count == 0)
        {
            return;
        }
        var promotedIds = promoted.Select(t => t.Id).ToHashSet();
        var moved = grandchildren.Where(t => promotedIds.Contains(t.ParentTaskId!.Value)).ToList();
        if (m is not null)
        {
            await m.SnapshotAsync([.. promoted, .. moved], ct);
        }
        foreach (var t in promoted)
        {
            t.ParentTaskId = null;
            t.Depth = 0;
            m?.Affect(t);
        }
        foreach (var t in moved)
        {
            t.Depth = 1;
            m?.Affect(t);
        }
    }

    /// <summary>兄弟の末尾の並び順（兄弟がいなければ 0）。</summary>
    private static async Task<double> MaxSortOrderAsync(TaskDeckDbContext db, Guid? parentId, CancellationToken ct) =>
        await db.Tasks.Where(t => t.ParentTaskId == parentId).MaxAsync(t => (double?)t.SortOrder, ct) ?? 0;

    /// <summary>value の前後に SortOrderMath.MinGap ぶんの隙間が無い（振り直しが要る）か。</summary>
    private static async Task<bool> IsCrowdedAsync(TaskDeckDbContext db, Guid? parentId, Guid selfId, double value, CancellationToken ct)
    {
        var siblings = db.Tasks.Where(t => t.ParentTaskId == parentId && t.DeletedAt == null && t.Id != selfId);
        var prev = await siblings.Where(t => t.SortOrder <= value).MaxAsync(t => (double?)t.SortOrder, ct);
        var next = await siblings.Where(t => t.SortOrder >= value).MinAsync(t => (double?)t.SortOrder, ct);
        return (prev is { } p && SortOrderMath.IsTooTight(p, value)) || (next is { } n && SortOrderMath.IsTooTight(value, n));
    }

    /// <summary>
    /// 同じ親の生きている兄弟を、並び順のまま 1024 刻みで振り直す（隙間が詰まったとき）。
    /// extra には、まだ DB にいない・別の親から移ってきたタスクを渡す。
    /// ponytail: ルート直下は「兄弟」が全ルートタスクになるので、1万件では1万行を書き直す。
    ///           同じ場所に30回ほど挿し込まないと起きない（1024 を 2 で割り続けて MinGap に届く回数）ので、まとめ直しはしない。
    /// </summary>
    private static async Task RenumberSiblingsAsync(
        TaskDeckDbContext db,
        Mutation m,
        Guid? parentId,
        IReadOnlyCollection<TaskItem> extra,
        CancellationToken ct)
    {
        var siblings = await db.Tasks.Where(t => t.ParentTaskId == parentId && t.DeletedAt == null).ToListAsync(ct);
        var known = siblings.Select(t => t.Id).ToHashSet();
        var all = siblings.Concat(extra.Where(t => !known.Contains(t.Id)))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToList();
        await m.SnapshotAsync(all, ct);
        for (var i = 0; i < all.Count; i++)
        {
            all[i].SortOrder = (i + 1) * SortOrderMath.Step;
            m.Affect(all[i]);
        }
    }

    /// <summary>取り消し用: スナップショットの全列を書き戻す（Id 以外）。</summary>
    private static void CopyScalars(TaskItem from, TaskItem to)
    {
        to.Title = from.Title;
        to.Notes = from.Notes;
        to.Status = from.Status;
        to.Priority = from.Priority;
        to.DueAt = from.DueAt;
        to.DueHasTime = from.DueHasTime;
        to.RemindAt = from.RemindAt;
        to.RemindOffsetMinutes = from.RemindOffsetMinutes;
        to.NotifiedAt = from.NotifiedAt;
        to.CompletedAt = from.CompletedAt;
        to.ParentTaskId = from.ParentTaskId;
        to.Depth = from.Depth;
        to.ProjectId = from.ProjectId;
        to.RecurrenceRuleId = from.RecurrenceRuleId;
        to.RecurrenceSeriesId = from.RecurrenceSeriesId;
        to.DurationMinutes = from.DurationMinutes;
        to.TemplateBatchId = from.TemplateBatchId;
        to.SortOrder = from.SortOrder;
        to.DeletedAt = from.DeletedAt;
    }

    private static RecurrenceRule NewRule(RecurrenceInput input, DateTime anchorUtc) => new()
    {
        RRule = input.RRule,
        AnchorAt = anchorUtc,
        BaseKind = input.BaseKind,
        EndKind = input.EndKind,
        EndDate = input.EndDate,
        MaxOccurrences = input.MaxOccurrences,
    };
}

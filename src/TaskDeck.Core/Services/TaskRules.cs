using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Time;

namespace TaskDeck.Core.Services;

/// <summary>期限の区分（一覧のグループ分けと色）。</summary>
public enum DueCategory
{
    None,
    Overdue,
    Today,
    Tomorrow,
    ThisWeek,
    Later,
}

/// <summary>期限・通知の判定。日付の比較はすべてローカル日付で行う。</summary>
public static class TaskRules
{
    /// <summary>
    /// 期限切れか。未完了のものだけが対象。
    /// 日付のみ: 期限日が今日より前。時刻あり: 期限時刻が現在より前。
    /// </summary>
    public static bool IsOverdue(TaskItem task, IClock clock)
    {
        if (!task.IsOpen || task.DueAt is not { } due)
        {
            return false;
        }
        return task.DueHasTime
            ? due < clock.UtcNow
            : due < clock.LocalDayStartUtc(clock.LocalToday());
    }

    public static DueCategory Categorize(TaskItem task, IClock clock)
    {
        if (task.DueAt is not { } due)
        {
            return DueCategory.None;
        }
        if (IsOverdue(task, clock))
        {
            return DueCategory.Overdue;
        }
        var today = clock.LocalToday();
        var day = clock.ToLocalDate(due);
        if (day <= today)
        {
            return DueCategory.Today;
        }
        if (day == today.AddDays(1))
        {
            return DueCategory.Tomorrow;
        }
        return day < today.AddDays(7) ? DueCategory.ThisWeek : DueCategory.Later;
    }

    /// <summary>日付のみの期限を保存形式（ローカル 0:00 の UTC）にする。</summary>
    public static DateTime DateOnlyDue(DateOnly day, IClock clock) => clock.LocalDayStartUtc(day);

    /// <summary>
    /// 相対通知から通知日時（UTC）を求める。offsetMinutes が null なら null（絶対指定の RemindAt を使う）。
    /// 基準は、時刻ありなら期限時刻、日付のみなら期限日の defaultReminderTime（既定 9:00）。
    /// </summary>
    public static DateTime? ComputeRemindAt(DateTime? dueAt, bool dueHasTime, int? offsetMinutes, TimeOnly defaultReminderTime, IClock clock)
    {
        if (offsetMinutes is not { } offset || dueAt is not { } due)
        {
            return null;
        }
        var baseUtc = dueHasTime
            ? due
            : clock.LocalToUtc(clock.ToLocalDate(due), defaultReminderTime);
        return baseUtc.AddMinutes(-offset);
    }
}

/// <summary>手動並び順（double）の計算。2要素の間は平均を入れるだけで、他の行を書き換えない。</summary>
public static class SortOrderMath
{
    public const double Step = 1024;

    /// <summary>この差より詰まったら兄弟を振り直す。</summary>
    public const double MinGap = 1e-6;

    /// <summary>before と after の間の値。どちらかが null なら端に Step だけ離して置く。</summary>
    public static double Between(double? before, double? after) => (before, after) switch
    {
        (null, null) => Step,
        ({ } b, null) => b + Step,
        (null, { } a) => a - Step,
        ({ } b, { } a) => b + ((a - b) / 2),
    };

    public static bool IsTooTight(double before, double after) => Math.Abs(after - before) < MinGap;
}

/// <summary>一覧の並べ方: 親が結果にあるタスクを、その親の直後（先に並ぶ兄弟の子孫の後ろ）に置く。</summary>
public sealed record TreeRow(TaskListRow Row, int Level);

public static class TaskTree
{
    /// <summary>
    /// rows の順（ルートどうしの順）を保ったまま、結果に親がいる行を親の下へ入れる。
    /// 兄弟の子は SortOrder → Id の順。Level は表示上の字下げ（結果の中のルートを 0 とする）。
    /// </summary>
    public static IReadOnlyList<TreeRow> Arrange(IReadOnlyList<TaskListRow> rows)
    {
        var ids = new HashSet<Guid>(rows.Select(r => r.Task.Id));
        var children = new Dictionary<Guid, List<TaskListRow>>();
        var roots = new List<TaskListRow>();
        foreach (var row in rows)
        {
            if (row.Task.ParentTaskId is { } parentId && parentId != row.Task.Id && ids.Contains(parentId))
            {
                if (!children.TryGetValue(parentId, out var list))
                {
                    list = [];
                    children[parentId] = list;
                }
                list.Add(row);
            }
            else
            {
                roots.Add(row);
            }
        }
        foreach (var list in children.Values)
        {
            list.Sort((a, b) =>
            {
                var c = a.Task.SortOrder.CompareTo(b.Task.SortOrder);
                return c != 0 ? c : a.Task.Id.CompareTo(b.Task.Id);
            });
        }

        var result = new List<TreeRow>(rows.Count);
        var visited = new HashSet<Guid>();
        foreach (var root in roots)
        {
            Visit(root, 0);
        }
        return result;

        void Visit(TaskListRow row, int level)
        {
            if (!visited.Add(row.Task.Id))
            {
                return;
            }
            result.Add(new TreeRow(row, level));
            if (children.TryGetValue(row.Task.Id, out var kids))
            {
                foreach (var kid in kids)
                {
                    Visit(kid, level + 1);
                }
            }
        }
    }
}

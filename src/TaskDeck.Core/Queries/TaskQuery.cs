using System.Text.Json.Serialization;

namespace TaskDeck.Core.Queries;

/// <summary>どの状態のタスクを出すか。</summary>
public enum TaskStatusFilter
{
    /// <summary>未着手＋進行中（削除済みを除く）。</summary>
    Open,
    /// <summary>完了＋中止（削除済みを除く）。</summary>
    Done,
    /// <summary>削除済み以外すべて。</summary>
    All,
    /// <summary>ゴミ箱（DeletedAt あり）。</summary>
    Deleted,
}

/// <summary>期限の条件。相対指定のまま保存し（保存済みフィルタ）、実行時に IClock で日付に解決する。</summary>
public enum DueFilter
{
    Any,
    /// <summary>期限切れ: 日付のみは今日より前、時刻ありは現在より前。</summary>
    Overdue,
    /// <summary>今日まで（期限切れを含む）= DueAt が明日 0:00 より前。「今日」ビュー。</summary>
    TodayOrOverdue,
    /// <summary>今日から7日間 = [今日 0:00, 7日後 0:00)。「予定」ビュー。</summary>
    Next7Days,
    NoDueDate,
    HasDueDate,
    /// <summary>DueFrom〜DueTo（ローカル日付、両端を含む。片側 null は開放）。</summary>
    Range,
}

public enum TaskSortKey
{
    /// <summary>手動の並び順（SortOrder）。</summary>
    Manual,
    /// <summary>期限（期限なしは最後）。</summary>
    Due,
    /// <summary>優先度（既定は高い順にするので Descending=true で使う）。</summary>
    Priority,
    Created,
    Title,
    /// <summary>閉じた日時（CompletedAt）。</summary>
    Completed,
    /// <summary>削除日時（DeletedAt）。</summary>
    Deleted,
}

/// <summary>
/// 一覧の絞り込み条件（設計書 5.3）。サイドバーの各ビュー・検索・複合絞り込み・保存済みフィルタはすべてこれに落とす。
/// JSON にそのまま保存できるよう、値はすべて init プロパティで持つ。
/// 同値のときは SortOrder、さらに Id を第2・第3キーにして毎回同じ並びにする。
/// </summary>
public sealed record TaskQuery
{
    public TaskStatusFilter Status { get; init; } = TaskStatusFilter.Open;
    /// <summary>空でなければ Status より優先し、削除済み以外でこの状態のものだけを出す（複合絞り込み用）。</summary>
    public IReadOnlyList<TaskItemStatus> Statuses { get; init; } = [];
    public DueFilter Due { get; init; } = DueFilter.Any;
    public DateOnly? DueFrom { get; init; }
    public DateOnly? DueTo { get; init; }
    public Guid? ProjectId { get; init; }
    /// <summary>true ならプロジェクトなしのタスクだけ。</summary>
    public bool WithoutProject { get; init; }
    /// <summary>すべてのタグを持つもの（AND）。</summary>
    public IReadOnlyList<Guid> TagIds { get; init; } = [];
    public Priority? MinPriority { get; init; }
    /// <summary>検索語。空白区切りの語をすべて含むもの（AND）。タイトルとメモが対象。</summary>
    public string? SearchText { get; init; }
    /// <summary>閉じた日（CompletedAt のローカル日付）がこの日以降のもの。完了済みビューの期間。</summary>
    public DateOnly? ClosedFrom { get; init; }
    public Guid? TemplateBatchId { get; init; }
    public TaskSortKey SortKey { get; init; } = TaskSortKey.Manual;
    public bool Descending { get; init; }
    /// <summary>
    /// true なら、条件に合ったタスクの子孫も（条件に合わなくても）結果に含める（IsContext=true）。
    /// 一覧で親の下にサブタスクを出すため。
    /// </summary>
    public bool IncludeSubtasks { get; init; } = true;

    [JsonIgnore]
    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);
}

public enum ViewKind
{
    Today,
    Upcoming,
    All,
    Completed,
    Trash,
    Project,
    Tag,
    SavedFilter,
    Calendar,
    Stats,
    /// <summary>使い捨てリスト（Id はリストの Id）。タスクの一覧ではない。</summary>
    Scratch,
}

/// <summary>サイドバーで選ぶ表示先。設定の「起動時ビュー」には ToString() の文字列で保存する。</summary>
public readonly record struct ViewKey(ViewKind Kind, Guid? Id = null)
{
    public static ViewKey Today => new(ViewKind.Today);
    public static ViewKey Upcoming => new(ViewKind.Upcoming);
    public static ViewKey All => new(ViewKind.All);
    public static ViewKey Completed => new(ViewKind.Completed);
    public static ViewKey Trash => new(ViewKind.Trash);
    public static ViewKey Calendar => new(ViewKind.Calendar);
    public static ViewKey Stats => new(ViewKind.Stats);
    public static ViewKey ForProject(Guid id) => new(ViewKind.Project, id);
    public static ViewKey ForTag(Guid id) => new(ViewKind.Tag, id);
    public static ViewKey ForSavedFilter(Guid id) => new(ViewKind.SavedFilter, id);
    public static ViewKey ForScratch(Guid id) => new(ViewKind.Scratch, id);

    /// <summary>一覧（TaskListView）で表示するビューか。カレンダー・振り返り・使い捨てリストは別の画面。</summary>
    public bool IsTaskList => Kind is not (ViewKind.Calendar or ViewKind.Stats or ViewKind.Scratch);

    public override string ToString() => Id is { } id ? $"{Kind}:{id:D}" : Kind.ToString();

    public static bool TryParse(string? text, out ViewKey key)
    {
        key = Today;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Split(':', 2);
        if (!Enum.TryParse<ViewKind>(parts[0], ignoreCase: true, out var kind))
        {
            return false;
        }
        var needsId = kind is ViewKind.Project or ViewKind.Tag or ViewKind.SavedFilter or ViewKind.Scratch;
        if (needsId)
        {
            if (parts.Length < 2 || !Guid.TryParse(parts[1], out var id))
            {
                return false;
            }
            key = new ViewKey(kind, id);
            return true;
        }
        key = new ViewKey(kind);
        return true;
    }
}

/// <summary>組み込みビューの条件と見出し。保存済みフィルタは AppSettings.SavedFilters から引く。</summary>
public static class BuiltInViews
{
    public static TaskQuery QueryFor(ViewKey key) => key.Kind switch
    {
        ViewKind.Today => new TaskQuery { Status = TaskStatusFilter.Open, Due = DueFilter.TodayOrOverdue, SortKey = TaskSortKey.Due },
        ViewKind.Upcoming => new TaskQuery { Status = TaskStatusFilter.Open, Due = DueFilter.Next7Days, SortKey = TaskSortKey.Due },
        ViewKind.All => new TaskQuery { Status = TaskStatusFilter.Open, SortKey = TaskSortKey.Manual },
        ViewKind.Completed => new TaskQuery { Status = TaskStatusFilter.Done, SortKey = TaskSortKey.Completed, Descending = true, IncludeSubtasks = false },
        ViewKind.Trash => new TaskQuery { Status = TaskStatusFilter.Deleted, SortKey = TaskSortKey.Deleted, Descending = true, IncludeSubtasks = false },
        ViewKind.Project => new TaskQuery { Status = TaskStatusFilter.Open, ProjectId = key.Id, SortKey = TaskSortKey.Manual },
        ViewKind.Tag => new TaskQuery { Status = TaskStatusFilter.Open, TagIds = key.Id is { } tagId ? [tagId] : [], SortKey = TaskSortKey.Manual },
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "このビューは TaskQuery を持たない（保存済みフィルタは設定から引く）"),
    };

    /// <summary>ビューの見出し（プロジェクト・タグ・保存済みフィルタは名前を別に引く）。</summary>
    public static string TitleOf(ViewKind kind) => kind switch
    {
        ViewKind.Today => "今日",
        ViewKind.Upcoming => "予定",
        ViewKind.All => "すべて",
        ViewKind.Completed => "完了済み",
        ViewKind.Trash => "ゴミ箱",
        ViewKind.Calendar => "カレンダー",
        ViewKind.Stats => "振り返り",
        ViewKind.Project => "プロジェクト",
        ViewKind.Tag => "タグ",
        ViewKind.SavedFilter => "フィルタ",
        ViewKind.Scratch => "使い捨て",
        _ => kind.ToString(),
    };
}

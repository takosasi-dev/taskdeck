using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Controls;

/// <summary>絞り込みの選択肢1つ（チップ1個）。</summary>
public sealed partial class FilterOption<T>(T value, string label) : ObservableObject
{
    public T Value { get; } = value;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected;
}

public enum ProjectChoiceKind
{
    /// <summary>プロジェクトで絞らない。</summary>
    Any,

    /// <summary>プロジェクトなしのタスクだけ。</summary>
    None,

    /// <summary>このプロジェクトのタスクだけ。</summary>
    Project,
}

/// <summary>プロジェクトの選択肢。ColorHex はテーマに合わせた表示色（「指定しない」「なし」は null）。</summary>
public sealed record ProjectChoice(ProjectChoiceKind Kind, Guid? Id, string? ColorHex);

/// <summary>
/// 複合絞り込み（F-059）の条件の組み立て。画面（FilterPanel.xaml）から切り離してテストする。
/// 変えるのは TaskQuery の絞り込みの8項目（Statuses Due DueFrom DueTo ProjectId WithoutProject TagIds MinPriority）だけで、
/// 検索語・並び順・IncludeSubtasks・Status などはそのまま返す（INTERFACES 5.7）。
/// </summary>
public sealed partial class FilterPanelModel : ObservableObject
{
    public const int SavedFilterNameMaxLength = 50;

    private static readonly ProjectChoice AnyProject = new(ProjectChoiceKind.Any, null, null);
    private static readonly ProjectChoice NoProject = new(ProjectChoiceKind.None, null, null);

    /// <summary>選択肢に無い期限の条件（期限なし・期限あり）を受け取ったら、期限の選択を変えない限りそのまま返す。</summary>
    private DueFilter _unlistedDue = DueFilter.Any;

    [ObservableProperty]
    private DateTime? _rangeStart;

    [ObservableProperty]
    private DateTime? _rangeEnd;

    public FilterPanelModel()
    {
        Projects = [.. FixedProjectOptions()];
        Clear();
    }

    /// <summary>状態（複数選べる。何も選ばなければビューの既定のまま）。</summary>
    public IReadOnlyList<FilterOption<TaskItemStatus>> Statuses { get; } =
    [
        new(TaskItemStatus.NotStarted, "未着手"),
        new(TaskItemStatus.InProgress, "進行中"),
        new(TaskItemStatus.Completed, "完了"),
        new(TaskItemStatus.Cancelled, "中止"),
    ];

    /// <summary>優先度（選んだもの以上。1つだけ選ぶ）。「以上」は見出しに書き、チップは1行に収める。</summary>
    public IReadOnlyList<FilterOption<Priority?>> Priorities { get; } =
    [
        new(null, "指定なし"),
        new(Priority.Low, "低"),
        new(Priority.Medium, "中"),
        new(Priority.High, "高"),
        new(Priority.Urgent, "緊急"),
    ];

    /// <summary>期限（1つだけ選ぶ。「期間」のときは RangeStart〜RangeEnd）。</summary>
    public IReadOnlyList<FilterOption<DueFilter>> DueOptions { get; } =
    [
        new(DueFilter.Any, "指定なし"),
        new(DueFilter.Overdue, "期限切れ"),
        new(DueFilter.TodayOrOverdue, "今日まで"),
        new(DueFilter.Next7Days, "7日以内"),
        new(DueFilter.Range, "期間"),
    ];

    /// <summary>「期間」の選択肢（日付の欄を出すかどうかに使う）。</summary>
    public FilterOption<DueFilter> RangeOption => DueOptions[^1];

    /// <summary>
    /// 「指定しない」「プロジェクトなし」と、各プロジェクト（1つだけ選ぶ）。
    /// ドロップダウン（入れ子の Popup）にすると、閉じたときに外側の Popup まで閉じてしまうので、チップで並べる。
    /// </summary>
    public ObservableCollection<FilterOption<ProjectChoice>> Projects { get; }

    /// <summary>いま選んでいるプロジェクトの条件（何も選ばれていなければ「指定しない」）。</summary>
    public ProjectChoice SelectedProject => Projects.FirstOrDefault(o => o.IsSelected)?.Value ?? AnyProject;

    /// <summary>タグ（複数選べる。選んだものをすべて持つタスク）。</summary>
    public ObservableCollection<FilterOption<Guid>> Tags { get; } = [];

    /// <summary>プロジェクトとタグの選択肢を入れ替える（開くたびに最新を読む）。選択は <see cref="Load"/> で入れ直す。</summary>
    public void SetChoices(IEnumerable<Project> projects, IEnumerable<Tag> tags, bool dark)
    {
        Projects.Clear();
        foreach (var option in FixedProjectOptions())
        {
            Projects.Add(option);
        }
        foreach (var project in projects)
        {
            var label = project.IsArchived ? $"{project.Name}（アーカイブ）" : project.Name;
            Projects.Add(new FilterOption<ProjectChoice>(
                new ProjectChoice(ProjectChoiceKind.Project, project.Id, ProjectPalette.ForTheme(project.ColorHex, dark)),
                label));
        }
        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(new FilterOption<Guid>(tag.Id, "#" + tag.Name));
        }
    }

    /// <summary>
    /// 受け取った条件を選択に写す。選択肢に無いプロジェクト・タグ（消したもの）は選ばない
    /// （＝適用すると外れる。見えない条件で一覧が空になるのを防ぐ）。
    /// </summary>
    public void Load(TaskQuery query)
    {
        foreach (var option in Statuses)
        {
            option.IsSelected = query.Statuses.Contains(option.Value);
        }
        var minPriority = query.MinPriority is Priority.None ? null : query.MinPriority;
        Select(Priorities, value => value == minPriority);

        var listed = DueOptions.Any(o => o.Value == query.Due);
        _unlistedDue = listed ? DueFilter.Any : query.Due;
        Select(DueOptions, value => listed && value == query.Due);
        RangeStart = query.DueFrom?.ToDateTime(TimeOnly.MinValue);
        RangeEnd = query.DueTo?.ToDateTime(TimeOnly.MinValue);

        var project = query.WithoutProject
            ? NoProject
            : Projects.Select(o => o.Value).FirstOrDefault(p => p.Kind == ProjectChoiceKind.Project && p.Id == query.ProjectId) ?? AnyProject;
        Select(Projects, value => value == project);
        foreach (var tag in Tags)
        {
            tag.IsSelected = query.TagIds.Contains(tag.Value);
        }
    }

    /// <summary>query の絞り込みの8項目だけを、いまの選択に置き換えたもの。</summary>
    public TaskQuery Apply(TaskQuery query)
    {
        var due = DueOptions.FirstOrDefault(o => o.IsSelected)?.Value ?? _unlistedDue;
        DateOnly? from = null;
        DateOnly? to = null;
        if (due == DueFilter.Range)
        {
            from = RangeStart is { } start ? DateOnly.FromDateTime(start) : null;
            to = RangeEnd is { } end ? DateOnly.FromDateTime(end) : null;
            if (from > to)
            {
                (from, to) = (to, from);
            }
        }
        var project = SelectedProject;
        return query with
        {
            Statuses = Reuse(query.Statuses, [.. Statuses.Where(o => o.IsSelected).Select(o => o.Value)]),
            MinPriority = Priorities.FirstOrDefault(o => o.IsSelected)?.Value,
            Due = due,
            DueFrom = from,
            DueTo = to,
            ProjectId = project.Kind == ProjectChoiceKind.Project ? project.Id : null,
            WithoutProject = project.Kind == ProjectChoiceKind.None,
            TagIds = Reuse(query.TagIds, [.. Tags.Where(o => o.IsSelected).Select(o => o.Value)]),
        };
    }

    /// <summary>
    /// 「クリア」: 絞り込みの8項目をビューの条件（baseQuery）に戻す。null なら全部「指定なし」（TaskQuery の既定値）。
    /// 「今日」ビューなら期限は「今日まで」、プロジェクトのビューならそのプロジェクトが選ばれた状態に戻る（波2-E）。
    /// </summary>
    public void ResetTo(TaskQuery? baseQuery) => Load(baseQuery ?? new TaskQuery());

    /// <summary>絞り込みの選択を全部「指定なし」に戻す（適用するまで一覧は変わらない）。</summary>
    public void Clear()
    {
        foreach (var option in Statuses)
        {
            option.IsSelected = false;
        }
        Select(Priorities, value => value is null);
        _unlistedDue = DueFilter.Any;
        Select(DueOptions, value => value == DueFilter.Any);
        RangeStart = null;
        RangeEnd = null;
        Select(Projects, value => value == AnyProject);
        foreach (var tag in Tags)
        {
            tag.IsSelected = false;
        }
    }

    /// <summary>
    /// 「この条件を保存」（F-05A）。名前は前後の空白を除いて50文字まで。
    /// 検索語は入力欄のその場の状態なので、保存する条件には入れない（ビューの状態・並び順は入れる）。
    /// </summary>
    public OperationResult<SavedFilter> CreateSavedFilter(string? name, TaskQuery query)
    {
        var trimmed = TextNormalizer.TruncateGraphemes(TextNormalizer.ForName(name), SavedFilterNameMaxLength);
        if (trimmed.Length == 0)
        {
            return OperationResult<SavedFilter>.Fail("名前を入れてください。");
        }
        return OperationResult<SavedFilter>.Success(new SavedFilter { Name = trimmed, Query = Apply(query) with { SearchText = null } });
    }

    private static IEnumerable<FilterOption<ProjectChoice>> FixedProjectOptions() =>
    [
        new(AnyProject, "指定しない"),
        new(NoProject, "プロジェクトなし"),
    ];

    private static void Select<T>(IEnumerable<FilterOption<T>> options, Func<T, bool> match)
    {
        foreach (var option in options)
        {
            option.IsSelected = match(option.Value);
        }
    }

    /// <summary>中身が同じなら元の一覧をそのまま返す（TaskQuery は一覧を参照で比べるので、変えていないのに「変わった」にしない）。</summary>
    private static IReadOnlyList<T> Reuse<T>(IReadOnlyList<T> original, List<T> current) =>
        original.SequenceEqual(current) ? original : current;
}

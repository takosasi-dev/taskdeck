using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.ViewModels;

/// <summary>並び替えメニューの1項目（F-018）。</summary>
public sealed partial class SortOption(string label, TaskSortKey key) : ObservableObject
{
    public string Label { get; } = label;

    public TaskSortKey Key { get; } = key;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>並び替えの向き（昇順／降順）。</summary>
public sealed partial class SortDirectionOption(string label, bool descending) : ObservableObject
{
    public string Label { get; } = label;

    public bool Descending { get; } = descending;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>完了済みビューの期間。</summary>
public sealed partial class PeriodOption(string label, int? days) : ObservableObject
{
    public string Label { get; } = label;

    public int? Days { get; } = days;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>
/// 一覧の選択の変化。DroppedByReload=true は「読み直しで選択中の行が一覧から外れた」（詳細ペインで期限を変えた等）。
/// このときは詳細ペインを閉じずにそのタスクを出し続ける（ビューの切替では閉じる）。
/// TaskId は主に選んでいる行（詳細ペインに出すもの）、TaskIds は選んでいる行ぜんぶ（一覧の並び順。複数選択 S-10）。
/// </summary>
public sealed record ListSelection(Guid? TaskId, bool DroppedByReload = false)
{
    public IReadOnlyList<Guid> TaskIds { get; init; } = TaskId is { } id ? [id] : [];
}

/// <summary>空状態のボタンが押したときにすること。</summary>
public enum EmptyStateAction
{
    None,
    /// <summary>「予定を見る」（今日ビューが空）。</summary>
    ShowUpcoming,
    /// <summary>「すべてのタスクから探す」（検索が0件）。</summary>
    SearchAllTasks,
    /// <summary>「絞り込みを解除」（絞り込みで0件）。</summary>
    ClearFilter,
}

/// <summary>
/// 中央の一覧（S-01）。ビューごとのグループ分け・並べ替え・検索・絞り込み・行の操作を持つ。
/// View の型（Brush・Visibility など）は持たない。
/// </summary>
public sealed partial class TaskListViewModel : ObservableObject
{
    /// <summary>完了して消えるビューで、行を残しておく時間（UI 設計書 7章）。</summary>
    public const int CompleteFadeMs = 167;

    /// <summary>変更通知をまとめる時間。</summary>
    public const int ChangeDebounceMs = 50;

    /// <summary>これを超えて読み込みが続いたときだけスケルトンを出す（UI 設計書 15.3）。</summary>
    public const int SkeletonDelayMs = 200;

    /// <summary>
    /// スケルトンを出すまでの待ち。null なら出さない（テスト用: UI スレッドが無いと、待ちの続きが読み込みの続きと別のスレッドで走って行を取り合うため）。
    /// </summary>
    internal TimeSpan? SkeletonDelay { get; init; } = TimeSpan.FromMilliseconds(SkeletonDelayMs);

    /// <summary>完了済みビューの既定の期間（日）。</summary>
    public const int DefaultCompletedPeriodDays = 30;

    /// <summary>「すべてのタスクから探す」の条件（削除済み以外のすべての状態）。</summary>
    private static readonly TaskQuery AllTasksQuery = new() { Status = TaskStatusFilter.All, SortKey = TaskSortKey.Due, IncludeSubtasks = false };

    // 選択中の印（IsCurrent）を持つので、項目はインスタンスごとに作る
    private readonly SortOption[] _commonSortOptions =
    [
        new("手動", TaskSortKey.Manual),
        new("期限", TaskSortKey.Due),
        new("優先度", TaskSortKey.Priority),
        new("作成日", TaskSortKey.Created),
        new("タイトル", TaskSortKey.Title),
    ];

    private readonly SortOption _completedSortOption = new("完了日", TaskSortKey.Completed);

    private readonly SortOption _deletedSortOption = new("削除日", TaskSortKey.Deleted);

    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projectRepository;
    private readonly ITagRepository _tagRepository;
    private readonly ITemplateRepository _templateRepository;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly UndoService _undo;
    private readonly ThemeService _theme;
    private readonly QuickInputParser _parser;
    private readonly ILogger<TaskListViewModel> _logger;
    private readonly Debouncer _reload;

    private IReadOnlyDictionary<Guid, ProjectLook> _projects = new Dictionary<Guid, ProjectLook>();
    private IReadOnlyDictionary<Guid, string> _tagNames = new Dictionary<Guid, string>();
    private bool _lookupsDirty = true;
    private int _loadToken;
    private bool _loading;
    private bool _viewSwitching;
    private bool _suppressSelectionEvent;
    private Guid? _selectionDuringLoad;
    private Guid? _lastNotifiedSelection;
    private EmptyStateAction _emptyAction;

    /// <summary>自分の操作（削除・完了）で選択中の行が消えるとき、次に選ぶ位置。</summary>
    private (Guid Id, int Index)? _pendingRemoval;

    public TaskListViewModel(
        ITaskRepository tasks,
        IProjectRepository projects,
        ITagRepository tags,
        ITemplateRepository templates,
        ISettingsStore settings,
        IClock clock,
        UndoService undo,
        ThemeService theme,
        QuickInputParser parser,
        ILogger<TaskListViewModel> logger)
    {
        _tasks = tasks;
        _projectRepository = projects;
        _tagRepository = tags;
        _templateRepository = templates;
        _settings = settings;
        _clock = clock;
        _undo = undo;
        _theme = theme;
        _parser = parser;
        _logger = logger;
        _reload = new Debouncer(ReloadAsync);
        _theme.ThemeChanged += (_, _) => Repaint();
        SortOptions = _commonSortOptions;
        MarkCurrentOptions();
    }

    public RowCollection Rows { get; } = [];

    public AddTaskRowViewModel AddRow { get; } = new();

    /// <summary>並び替えの候補（完了済み・ゴミ箱では「完了日」「削除日」が先頭に付く）。</summary>
    public IReadOnlyList<SortOption> SortOptions { get; private set; }

    public IReadOnlyList<SortDirectionOption> SortDirections { get; } =
    [
        new("昇順", false),
        new("降順", true),
    ];

    public IReadOnlyList<PeriodOption> PeriodOptions { get; } =
    [
        new("今日", 1),
        new("7日", 7),
        new("30日", DefaultCompletedPeriodDays),
        new("すべて", null),
    ];

    /// <summary>今どのビューを出しているか。切り替えは ShowAsync。</summary>
    public ViewKey CurrentView { get; private set; } = ViewKey.Today;

    /// <summary>サイドバーの件数（空状態の文言に使う）。</summary>
    public ViewCounts Counts { get; set; } = ViewCounts.Empty;

    /// <summary>いま効いている検索語（空なら null）。</summary>
    public string? SearchText { get; private set; }

    /// <summary>検索の範囲をビューではなく「すべてのタスク」にしている（空状態の「すべてのタスクから探す」）。</summary>
    public bool SearchAllTasks { get; private set; }

    /// <summary>絞り込みパネル（波2-G）で指定された条件。null ならビューの既定。</summary>
    public TaskQuery? ActiveFilter { get; private set; }

    /// <summary>最後に一覧を出した条件（ShellService.CurrentListQuery に入れる）。</summary>
    public TaskQuery? LastQuery { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRow))]
    private ListItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualSort))]
    [NotifyPropertyChangedFor(nameof(CurrentSortLabel))]
    private TaskSortKey _sortKey = TaskSortKey.Due;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSortLabel))]
    private bool _sortDescending;

    [ObservableProperty]
    private int? _completedPeriodDays = DefaultCompletedPeriodDays;

    /// <summary>200ms を超えた読み込み中（スケルトン4行）。</summary>
    [ObservableProperty]
    private bool _isSkeletonVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPurgeAll))]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyTitle = "";

    [ObservableProperty]
    private string _emptyMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyAction))]
    private string? _emptyActionLabel;

    /// <summary>ビューの見出し（MainViewModel が入れる）。</summary>
    [ObservableProperty]
    private string _title = "今日";

    [ObservableProperty]
    private string _emptyIconKey = "IconList";

    /// <summary>初回起動の空状態（案内として地を Surface.Selected にする）。</summary>
    [ObservableProperty]
    private bool _isFirstRunEmpty;

    /// <summary>見出しの補足（「9月22日（火）・残り 7 件」）。</summary>
    [ObservableProperty]
    private string _subtitle = "";

    /// <summary>検索欄の右に出す件数（検索中のみ）。</summary>
    [ObservableProperty]
    private string? _searchSummary;

    /// <summary>絞り込みパネルの条件が効いている（見出しに「絞り込み中」と解除ボタン）。</summary>
    [ObservableProperty]
    private bool _isFiltered;

    public TaskRowViewModel? SelectedRow => SelectedItem as TaskRowViewModel;

    public bool IsManualSort => SortKey == TaskSortKey.Manual;

    public bool IsTrashView => CurrentView.Kind == ViewKind.Trash;

    public bool IsCompletedView => CurrentView.Kind == ViewKind.Completed;

    public bool IsTodayView => CurrentView.Kind == ViewKind.Today;

    public bool HasEmptyAction => EmptyActionLabel is not null;

    /// <summary>「ゴミ箱を空にする」を押せるか（空のときは押せない）。</summary>
    public bool CanPurgeAll => !IsEmpty;

    public string CurrentSortLabel
    {
        get
        {
            var label = SortOptions.FirstOrDefault(o => o.Key == SortKey)?.Label ?? "手動";
            // 既定の向き（期限・手動などは昇順、優先度・完了日・削除日は降順）と違うときだけ向きを書く
            var suffix = SortDescending == DefaultDescending(SortKey) ? "" : SortDescending ? "（降順）" : "（昇順）";
            return label + "順" + suffix;
        }
    }

    /// <summary>選択が変わった（詳細ペインと ShellService.SelectedTaskIds が拾う）。</summary>
    public event EventHandler<ListSelection>? SelectedTaskChanged;

    /// <summary>空状態のボタンのうち、一覧の外で処理するもの（予定を見る／すべてのタスクから探す）。</summary>
    public event EventHandler<EmptyStateAction>? EmptyActionInvoked;

    /// <summary>一覧を作り直す直前／直後（View がスクロール位置を保つ）。</summary>
    public event EventHandler? RowsReloading;

    public event EventHandler? RowsReloaded;

    /// <summary>
    /// ビューを切り替えて読み直す（並び順・絞り込み・検索の範囲はビューの既定に戻す。検索語はそのまま）。
    /// preferredSelection は、一覧で何も選んでいないときに選び直したいタスク（詳細ペインに出ているもの）。
    /// </summary>
    public async Task ShowAsync(ViewKey key, Guid? preferredSelection = null)
    {
        _selectionDuringLoad = SelectedRow?.Id ?? preferredSelection;
        CurrentView = key;
        var baseQuery = BaseQuery(key);
        SortOptions = key.Kind switch
        {
            ViewKind.Completed => [_completedSortOption, .. _commonSortOptions],
            ViewKind.Trash => [_deletedSortOption, .. _commonSortOptions],
            _ => _commonSortOptions,
        };
        SortKey = baseQuery.SortKey;
        SortDescending = baseQuery.Descending;
        ActiveFilter = null;
        IsFiltered = false;
        SearchAllTasks = false;
        _pendingRemoval = null;
        _viewSwitching = true;
        MarkCurrentOptions();
        OnPropertyChanged(nameof(SortOptions));
        OnPropertyChanged(nameof(IsTrashView));
        OnPropertyChanged(nameof(IsCompletedView));
        OnPropertyChanged(nameof(IsTodayView));
        OnPropertyChanged(nameof(IsManualSort));
        OnPropertyChanged(nameof(CurrentSortLabel));
        await ReloadAsync();
    }

    /// <summary>検索語を変えて読み直す。allTasks=true で範囲を「すべてのタスク」にする。</summary>
    public async Task SetSearchAsync(string? text, bool allTasks = false)
    {
        SearchText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        SearchAllTasks = allTasks && SearchText is not null;
        await ReloadAsync();
    }

    /// <summary>絞り込みパネルの結果を当てる（null かビューの既定と同じなら解除）。</summary>
    public async Task ApplyFilterAsync(TaskQuery? query)
    {
        var baseQuery = BaseQuery(CurrentView);
        ActiveFilter = query is null || IsSameFilter(query, baseQuery)
            ? null
            // 検索語・並び順・期間は一覧の側で毎回かけ直すので、絞り込みの項目だけを残す
            : query with { SearchText = null, IncludeSubtasks = baseQuery.IncludeSubtasks, ClosedFrom = null };
        IsFiltered = ActiveFilter is not null;
        await ReloadAsync();
    }

    /// <summary>delayMs 後に読み直す（重なった要求はまとめ、いちばん遅い時刻に合わせる）。</summary>
    public void ScheduleReload(int delayMs) => _reload.Schedule(delayMs);

    /// <summary>プロジェクト・タグの名前を引き直す（変更通知で呼ぶ）。</summary>
    public void InvalidateLookups() => _lookupsDirty = true;

    /// <summary>いま一覧に出す条件（ビュー＋絞り込み＋検索＋並び順＋期間）。</summary>
    public TaskQuery BuildQuery()
    {
        var searching = SearchText is not null;
        var query = searching && SearchAllTasks ? AllTasksQuery : ActiveFilter ?? BaseQuery(CurrentView);
        query = query with { SortKey = SortKey, Descending = SortDescending, SearchText = SearchText };
        if (searching)
        {
            // 検索結果は一致した行だけを出す（一致した親の子を添えない）
            query = query with { IncludeSubtasks = false };
        }
        if (!(searching && SearchAllTasks) && CurrentView.Kind == ViewKind.Completed && CompletedPeriodDays is { } days)
        {
            query = query with { ClosedFrom = _clock.LocalToday().AddDays(-(days - 1)) };
        }
        return query;
    }

    public async Task ReloadAsync()
    {
        var token = ++_loadToken;
        _loading = true;
        var watch = Stopwatch.StartNew();
        // スケルトンを出すときに行を消すので、選択は読み込みを始めた時点のものを覚えておく（複数選択のほかの行も）
        var selectedId = SelectedRow?.Id ?? _selectionDuringLoad;
        _selectionDuringLoad = selectedId;
        var otherSelectedIds = _selected.Count > 1 ? [.. _selected.Select(r => r.Id)] : _otherSelectionDuringLoad;
        _otherSelectionDuringLoad = otherSelectedIds;
        ShowSkeletonAfterDelayAsync(token);

        await EnsureLookupsAsync();
        var query = BuildQuery();
        var view = CurrentView.Kind;
        var terms = query.HasSearch
            ? TextNormalizer.ForSearch(query.SearchText).Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            : [];
        var context = new RowBuildContext(_clock, _tagNames, _projects, _theme.IsDark, _settings.Current.Appearance.CompactRows, terms);

        var rows = await _tasks.QueryAsync(query);
        if (token != _loadToken)
        {
            return;
        }
        if (!query.HasSearch && view is not (ViewKind.Completed or ViewKind.Trash))
        {
            // テンプレートの一群の見出しに出す名前（一群が一覧に2件以上あるときだけテンプレートを引く F-15B）
            context = context with { BatchNames = await BatchNamesAsync(rows) };
            if (token != _loadToken)
            {
                return;
            }
        }
        var queryMs = watch.ElapsedMilliseconds;
        // 並べ替えと行の組み立ては件数に比例するので UI スレッドの外で行う（1万件でも画面を止めない）
        var built = await Task.Run(() => TaskListBuilder.Build(TaskTree.Arrange(rows), view, context));
        if (token != _loadToken)
        {
            return;
        }
        var buildMs = watch.ElapsedMilliseconds - queryMs;
        var applyStart = watch.ElapsedMilliseconds;
        ApplyRows(built, query, selectedId, otherSelectedIds);
        _selectionDuringLoad = null;
        _otherSelectionDuringLoad = [];
        _loading = false;
        _viewSwitching = false;
        // ログにタスク名・検索語は出さない（ビューの種類と件数と時間だけ）
        _logger.LogDebug(
            "一覧を読み込みました: {View} {Count}件 合計 {Milliseconds} ms（問い合わせ {QueryMs} ms・組み立て {BuildMs} ms・UI への反映 {ApplyMs} ms・検索 {Searching}）",
            CurrentView.Kind,
            rows.Count,
            watch.ElapsedMilliseconds,
            queryMs,
            buildMs,
            watch.ElapsedMilliseconds - applyStart,
            query.HasSearch);
    }

    // ---- 行の操作 ----

    /// <summary>Space: 選んでいる行の完了を切り替える。複数選択中は選んだ行をまとめて完了にする。</summary>
    [RelayCommand]
    private async Task ToggleCompleteAsync(TaskRowViewModel? row)
    {
        if (IsMultiSelection && (row is null || _selected.Contains(row)))
        {
            await CompleteSelectedAsync();
            return;
        }
        row ??= SelectedRow;
        if (row is not null)
        {
            await SetCompletedAsync(row, !row.IsClosed);
        }
    }

    /// <summary>
    /// 完了（true）／未完了に戻す（false）。繰り返しの次回ができたら取り消しトーストを出す（UI 設計書 13.1）。
    /// 未完了のサブタスクがある親を完了にするときは、サブタスクもまとめて完了にするかを確認する（F-024）。
    /// 書き込んだら true（キャンセルした・変わらなかったら false。View はチェックを元に戻す）。
    /// </summary>
    public async Task<bool> SetCompletedAsync(TaskRowViewModel row, bool completed)
    {
        if (row.IsInTrash || row.IsClosed == completed)
        {
            return false;
        }
        IReadOnlyList<Guid> ids = [row.Id];
        if (completed && row.HasOpenSubtasks)
        {
            if (await SubtaskCompletion.ResolveAsync(_tasks, ids, ids, DisplayText.Quote(row.Title), Confirm) is not { } resolved)
            {
                return false;
            }
            ids = resolved;
        }
        var result = await _tasks.SetCompletedAsync(ids, completed);
        if (result.Changes.IsEmpty)
        {
            return false;
        }
        if (completed && result.Created.Count > 0)
        {
            var next = result.Created.Select(t => t.DueAt).FirstOrDefault(d => d is not null);
            var label = next is { } due
                ? "完了しました・次回は " + DisplayText.DateWithWeekday(_clock.ToLocalDate(due))
                : "完了しました・次回を作りました";
            _undo.Record(result, label, UndoKind.Toggle, showToast: true);
        }
        else
        {
            var label = DisplayText.Quote(row.Title) + (completed ? "を完了しました" : "を未完了に戻しました");
            if (ids.Count > 1)
            {
                label = string.Create(CultureInfo.InvariantCulture, $"{DisplayText.Quote(row.Title)}とサブタスク {ids.Count - 1} 件を完了しました");
            }
            _undo.Record(result, label, UndoKind.Toggle, showToast: false);
        }
        // 親の下に添えたサブタスクは、完了しても親の下に取り消し線つきで残るので消さない
        if (row.Level == 0 && LeavesView(completed))
        {
            // 押し間違いに気づけるよう、消える行は 167ms 残してから消す（UI 設計書 7章）
            row.IsFading = true;
            RememberRemoval(row);
            ScheduleReload(CompleteFadeMs);
        }
        return true;
    }

    /// <summary>Delete・一括ツールバーの削除。複数選択中はまとめてゴミ箱へ移し、取り消しは1段（トースト「3 件を削除しました」）。</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var rows = _selected.Where(r => !r.IsInTrash).ToList();
        if (rows.Count == 1)
        {
            await DeleteAsync(rows[0].Id, rows[0].Title);
            return;
        }
        if (rows.Count == 0)
        {
            return;
        }
        if (SelectedRow is { } primary)
        {
            RememberRemoval(primary);
        }
        var result = await _tasks.SoftDeleteAsync([.. rows.Select(r => r.Id)]);
        _undo.Record(result, Count(rows.Count) + "を削除しました", UndoKind.Bulk, showToast: true);
    }

    /// <summary>ゴミ箱へ移して、取り消しトースト「「〇〇」を削除しました」を出す。</summary>
    public async Task DeleteAsync(Guid id, string title)
    {
        if (FindRow(id) is { } row)
        {
            RememberRemoval(row);
        }
        var result = await _tasks.SoftDeleteAsync([id]);
        _undo.Record(result, DisplayText.Quote(title) + "を削除しました", UndoKind.Delete, showToast: true);
    }

    [RelayCommand]
    private async Task RestoreAsync(TaskRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null || !row.IsInTrash)
        {
            return;
        }
        RememberRemoval(row);
        var result = await _tasks.RestoreAsync([row.Id]);
        _undo.Record(result, DisplayText.Quote(row.Title) + "を元に戻しました", UndoKind.Delete, showToast: false);
    }

    /// <summary>ゴミ箱から完全に削除する（取り消せない。確認は View 側で取る）。</summary>
    public async Task PurgeAsync(TaskRowViewModel row)
    {
        RememberRemoval(row);
        var count = await _tasks.PurgeAsync([row.Id]);
        _logger.LogInformation("完全に削除しました（{Count}件）", count);
        ScheduleReload(0);
    }

    /// <summary>ゴミ箱を空にする（取り消せない。確認は View 側で取る）。</summary>
    public async Task PurgeAllAsync()
    {
        var count = await _tasks.PurgeDeletedAsync(null);
        _logger.LogInformation("ゴミ箱を空にしました（{Count}件）", count);
        ScheduleReload(0);
    }

    /// <summary>その場で名前を変える（F2／Enter）。</summary>
    [RelayCommand]
    private void BeginRename(TaskRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null || row.IsInTrash || row.IsEditing)
        {
            return;
        }
        row.EditText = row.Title;
        row.IsEditing = true;
    }

    /// <summary>名前の変更を確定する（Enter とフォーカスアウトの両方から呼ばれるので、2回目は何もしない）。</summary>
    public async Task CommitRenameAsync(TaskRowViewModel row)
    {
        if (!row.IsEditing)
        {
            return;
        }
        row.IsEditing = false;
        var text = TextNormalizer.ForTitle(row.EditText, TaskItem.TitleMaxLength);
        if (text.Length == 0 || text == row.Title)
        {
            return;
        }
        var before = row.Title;
        var result = await _tasks.UpdateAsync(row.Id, t => t.Title = text);
        _undo.Record(result, DisplayText.Quote(before) + "の名前を変えました", UndoKind.TextEdit, showToast: false);
        row.Title = result.Affected.FirstOrDefault(t => t.Id == row.Id)?.Title ?? text;
    }

    public static void CancelRename(TaskRowViewModel row) => row.IsEditing = false;

    /// <summary>手動並び替え（Ctrl+↑ / Ctrl+↓）。手動順のときだけ効く。並びは取り消しの対象外（UI 設計書 13.3）。</summary>
    public async Task MoveSelectedAsync(bool up)
    {
        if (!IsManualSort || SelectedRow is not { } row || row.IsInTrash)
        {
            return;
        }
        var siblings = Rows.OfType<TaskRowViewModel>()
            .Where(r => r.Level == row.Level && r.Task.ParentTaskId == row.Task.ParentTaskId)
            .ToList();
        var index = siblings.IndexOf(row);
        var target = up ? index - 1 : index + 1;
        if (index < 0 || target < 0 || target >= siblings.Count)
        {
            return;
        }
        var neighbour = siblings[target];
        var beyond = up
            ? (target - 1 >= 0 ? siblings[target - 1] : null)
            : (target + 1 < siblings.Count ? siblings[target + 1] : null);
        var order = up
            ? SortOrderMath.Between(beyond?.Task.SortOrder, neighbour.Task.SortOrder)
            : SortOrderMath.Between(neighbour.Task.SortOrder, beyond?.Task.SortOrder);
        await _tasks.ReorderAsync(row.Id, order);
    }

    /// <summary>
    /// インライン追加。パーサを通し、ビューに合わせた既定を足す（F-044）。
    /// いまの一覧に出ないタスクになったとき（「明日」と書いた等）だけ、行き先をトーストで知らせる。
    /// </summary>
    public async Task<bool> AddFromInputAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var request = ApplyViewDefaults(_parser.Parse(text).ToRequest());
        var result = await _tasks.AddAsync(request);
        if (result.Created.FirstOrDefault() is not { } created)
        {
            return false;
        }
        _logger.LogInformation("タスクを追加しました {Id}", created.Id);
        await ReloadAsync();
        var visible = FindRow(created.Id) is not null;
        var label = DisplayText.Quote(created.Title) + "を追加しました";
        if (!visible && created.DueAt is { } due)
        {
            label += "（期限 " + DisplayText.DueShort(due, created.DueHasTime, _clock) + "）";
        }
        _undo.Record(result, label, UndoKind.Other, showToast: !visible);
        return true;
    }

    [RelayCommand]
    private async Task SetSortAsync(SortOption? option)
    {
        if (option is null)
        {
            return;
        }
        if (option.Key != SortKey)
        {
            SortKey = option.Key;
            SortDescending = DefaultDescending(option.Key);
        }
        MarkCurrentOptions();
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task SetSortDirectionAsync(SortDirectionOption? option)
    {
        if (option is null || option.Descending == SortDescending)
        {
            return;
        }
        SortDescending = option.Descending;
        MarkCurrentOptions();
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task SetPeriodAsync(PeriodOption? option)
    {
        if (option is null)
        {
            return;
        }
        CompletedPeriodDays = option.Days;
        MarkCurrentOptions();
        await ReloadAsync();
    }

    [RelayCommand]
    private Task ClearFilterAsync() => ApplyFilterAsync(null);

    [RelayCommand]
    private async Task InvokeEmptyActionAsync()
    {
        if (_emptyAction == EmptyStateAction.ClearFilter)
        {
            await ApplyFilterAsync(null);
            return;
        }
        if (_emptyAction != EmptyStateAction.None)
        {
            EmptyActionInvoked?.Invoke(this, _emptyAction);
        }
    }

    // ---- 内部 ----

    /// <summary>
    /// 主に選んでいる行（ListBox の SelectedItem）が変わった。複数選択の中の1行が主になっただけなら選択の集合はそのまま
    /// （Ctrl+クリックで先頭を外したときなど。集合は View が SetSelection で直す）。それ以外はその1行だけの選択にする。
    /// </summary>
    partial void OnSelectedItemChanged(ListItemViewModel? oldValue, ListItemViewModel? newValue)
    {
        var row = newValue as TaskRowViewModel;
        if (row is null || !_selected.Contains(row))
        {
            ReplaceSelection(row is null ? [] : [row]);
        }
        if (!_suppressSelectionEvent)
        {
            NotifySelection(dropped: false);
        }
    }

    private static bool DefaultDescending(TaskSortKey key) =>
        key is TaskSortKey.Priority or TaskSortKey.Completed or TaskSortKey.Deleted;

    private TaskQuery BaseQuery(ViewKey key)
    {
        if (key.Kind == ViewKind.SavedFilter)
        {
            var saved = _settings.Current.SavedFilters.FirstOrDefault(f => f.Id == key.Id);
            return saved?.Query ?? new TaskQuery();
        }
        return key.IsTaskList ? BuiltInViews.QueryFor(key) : new TaskQuery();
    }

    /// <summary>絞り込みの項目だけを比べる（検索語・並び順・期間は比べない）。</summary>
    private static bool IsSameFilter(TaskQuery a, TaskQuery b) =>
        a.Status == b.Status
        && a.Statuses.SequenceEqual(b.Statuses)
        && a.Due == b.Due
        && a.DueFrom == b.DueFrom
        && a.DueTo == b.DueTo
        && a.ProjectId == b.ProjectId
        && a.WithoutProject == b.WithoutProject
        && a.TagIds.SequenceEqual(b.TagIds)
        && a.MinPriority == b.MinPriority;

    /// <summary>この切替で行がいまの一覧から外れるか（未完了のビューで完了、完了済みビューで未完了に戻す）。</summary>
    private bool LeavesView(bool completed)
    {
        var query = BuildQuery();
        if (query.Statuses.Count > 0)
        {
            return false;
        }
        return query.Status switch
        {
            TaskStatusFilter.Open => completed,
            TaskStatusFilter.Done => !completed,
            _ => false,
        };
    }

    private void RememberRemoval(TaskRowViewModel row)
    {
        if (SelectedRow == row)
        {
            _pendingRemoval = (row.Id, Rows.IndexOf(row));
        }
    }

    private NewTaskRequest ApplyViewDefaults(NewTaskRequest request)
    {
        switch (CurrentView.Kind)
        {
            case ViewKind.Today or ViewKind.Upcoming when request.DueAt is null:
                return request with { DueAt = TaskRules.DateOnlyDue(_clock.LocalToday(), _clock), DueHasTime = false };
            case ViewKind.Project when request.ProjectId is null && request.ProjectName is null:
                return request with { ProjectId = CurrentView.Id };
            case ViewKind.Tag when CurrentView.Id is { } tagId && !request.TagIds.Contains(tagId):
                return request with { TagIds = [.. request.TagIds, tagId] };
            default:
                return request;
        }
    }

    private async Task EnsureLookupsAsync()
    {
        if (!_lookupsDirty)
        {
            return;
        }
        var projects = await _projectRepository.GetAllAsync(includeArchived: true);
        var tags = await _tagRepository.GetAllAsync();
        // 別スレッドで行を組み立てるので、中身を書き換えずに丸ごと差し替える
        _projects = projects.ToDictionary(p => p.Id, p => new ProjectLook(p.Name, p.ColorHex));
        _tagNames = tags.ToDictionary(t => t.Id, t => t.Name);
        _lookupsDirty = false;
    }

    private void Repaint()
    {
        foreach (var row in Rows.OfType<TaskRowViewModel>())
        {
            row.Repaint(_theme.IsDark);
        }
    }

    private void MarkCurrentOptions()
    {
        foreach (var option in SortOptions)
        {
            option.IsCurrent = option.Key == SortKey;
        }
        foreach (var option in SortDirections)
        {
            option.IsCurrent = option.Descending == SortDescending;
        }
        foreach (var option in PeriodOptions)
        {
            option.IsCurrent = option.Days == CompletedPeriodDays;
        }
    }

    private async void ShowSkeletonAfterDelayAsync(int token)
    {
        if (SkeletonDelay is not { } delay)
        {
            return;
        }
        await Task.Delay(delay);
        if (token != _loadToken || !_loading)
        {
            return;
        }
        // ビューを切り替えたときだけ前のビューの行を消して形だけ出す。同じビューの読み直しでは行を残す（ちらつかせない）
        if (_viewSwitching || Rows.Count == 0)
        {
            _suppressSelectionEvent = true;
            try
            {
                Rows.ReplaceAll([]);
                SelectedItem = null;
            }
            finally
            {
                _suppressSelectionEvent = false;
            }
            IsEmpty = false;
            IsSkeletonVisible = true;
        }
    }

    private void ApplyRows(ListBuildResult built, TaskQuery query, Guid? selectedId, IReadOnlyList<Guid> otherSelectedIds)
    {
        LastQuery = query;
        var items = new List<ListItemViewModel>(built.Items.Count + 1);
        items.AddRange(built.Items);
        if (!IsCompletedView && !IsTrashView && SearchText is null)
        {
            items.Add(AddRow);
        }

        RowsReloading?.Invoke(this, EventArgs.Empty);
        _suppressSelectionEvent = true;
        try
        {
            Rows.Update(items);
            SelectedItem = PickSelection(selectedId);
            RestoreOtherSelection(otherSelectedIds);
        }
        finally
        {
            _suppressSelectionEvent = false;
        }
        if (SelectionDiffersFromNotified())
        {
            NotifySelection(dropped: SelectedRow is null && !_viewSwitching);
        }
        if (_selected.Count > 1)
        {
            // 行は作り直したので、ListBox の複数選択を新しい行で入れ直してもらう
            SelectionSyncRequested?.Invoke(this, EventArgs.Empty);
        }
        IsSkeletonVisible = false;
        UpdateSummaries(built);
        RowsReloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>読み直した後の選択: 同じタスクがあればそれ、自分の操作で消えたなら同じ位置の行、それ以外は選択なし。</summary>
    private TaskRowViewModel? PickSelection(Guid? selectedId)
    {
        if (selectedId is not { } id)
        {
            _pendingRemoval = null;
            return null;
        }
        if (FindRow(id) is { } same)
        {
            if (_pendingRemoval is { } pending && pending.Id != id)
            {
                _pendingRemoval = null;
            }
            return same;
        }
        if (_pendingRemoval is not { } removal || removal.Id != id)
        {
            _pendingRemoval = null;
            return null;
        }
        _pendingRemoval = null;
        var rows = Rows.ToList();
        for (var i = Math.Max(0, removal.Index); i < rows.Count; i++)
        {
            if (rows[i] is TaskRowViewModel after)
            {
                return after;
            }
        }
        for (var i = Math.Min(removal.Index, rows.Count) - 1; i >= 0; i--)
        {
            if (rows[i] is TaskRowViewModel before)
            {
                return before;
            }
        }
        return null;
    }

    private TaskRowViewModel? FindRow(Guid id)
    {
        foreach (var item in Rows)
        {
            if (item is TaskRowViewModel row && row.Id == id)
            {
                return row;
            }
        }
        return null;
    }

    private void UpdateSummaries(ListBuildResult built)
    {
        var searching = SearchText is not null;
        SearchSummary = searching ? string.Create(CultureInfo.InvariantCulture, $"{built.Matched} 件") : null;

        Subtitle = searching && SearchAllTasks
            ? string.Create(CultureInfo.InvariantCulture, $"すべてのタスクから「{SearchText}」を探しました・{built.Matched} 件")
            : CurrentView.Kind switch
            {
                ViewKind.Today => DisplayText.DateWithWeekday(_clock.LocalToday()) + string.Create(CultureInfo.InvariantCulture, $"・残り {built.MatchedOpen} 件"),
                ViewKind.Upcoming => string.Create(CultureInfo.InvariantCulture, $"今日から7日間・{built.Matched} 件"),
                ViewKind.Completed => string.Create(CultureInfo.InvariantCulture, $"{PeriodLabel()}・{built.Matched} 件"),
                ViewKind.Trash => string.Create(CultureInfo.InvariantCulture, $"{built.Matched} 件・削除から30日で完全に消えます"),
                _ => string.Create(CultureInfo.InvariantCulture, $"{built.Matched} 件"),
            };

        IsEmpty = built.Matched == 0;
        IsFirstRunEmpty = false;
        _emptyAction = EmptyStateAction.None;
        if (!IsEmpty)
        {
            return;
        }
        if (searching)
        {
            EmptyIconKey = "IconSearch";
            EmptyTitle = $"「{SearchText}」は見つかりません";
            if (SearchAllTasks)
            {
                EmptyMessage = "すべてのタスクを探しました";
                SetEmptyAction(null, EmptyStateAction.None);
            }
            else
            {
                EmptyMessage = ViewLabel() + "の中だけを探しています";
                SetEmptyAction("すべてのタスクから探す", EmptyStateAction.SearchAllTasks);
            }
            return;
        }
        if (ActiveFilter is not null)
        {
            EmptyIconKey = "IconFilter";
            EmptyTitle = "条件に合うタスクはありません";
            EmptyMessage = "絞り込みを解除すると、元の一覧に戻ります";
            SetEmptyAction("絞り込みを解除", EmptyStateAction.ClearFilter);
            return;
        }
        switch (CurrentView.Kind)
        {
            case ViewKind.Today when Counts.AllOpen == 0:
                EmptyIconKey = "IconPlus";
                EmptyTitle = "最初のタスクを入れましょう";
                EmptyMessage = "どのアプリを使っていても Ctrl+Shift+Space で呼び出せます";
                SetEmptyAction(null, EmptyStateAction.None);
                IsFirstRunEmpty = true;
                break;
            case ViewKind.Today:
                EmptyIconKey = "IconCheckCircle";
                EmptyTitle = "今日のぶんは終わりです";
                EmptyMessage = string.Create(CultureInfo.InvariantCulture, $"明日以降の予定が {Counts.Upcoming} 件あります");
                SetEmptyAction("予定を見る", EmptyStateAction.ShowUpcoming);
                break;
            case ViewKind.Trash:
                EmptyIconKey = "IconTrash";
                EmptyTitle = "ゴミ箱は空です";
                EmptyMessage = "削除したタスクは 30 日で完全に消えます";
                SetEmptyAction(null, EmptyStateAction.None);
                break;
            case ViewKind.Completed:
                EmptyIconKey = "IconCheck";
                EmptyTitle = "この期間に完了したタスクはありません";
                EmptyMessage = "期間を広げると、前に片づけたものが出てきます";
                SetEmptyAction(null, EmptyStateAction.None);
                break;
            case ViewKind.Upcoming:
                EmptyIconKey = "IconCalendar";
                EmptyTitle = "この先7日間の予定はありません";
                EmptyMessage = "先の予定は「すべて」から確認できます";
                SetEmptyAction(null, EmptyStateAction.None);
                break;
            default:
                EmptyIconKey = "IconList";
                EmptyTitle = "タスクはありません";
                EmptyMessage = "Ctrl+N で追加できます";
                SetEmptyAction(null, EmptyStateAction.None);
                break;
        }
    }

    private void SetEmptyAction(string? label, EmptyStateAction action)
    {
        EmptyActionLabel = label;
        _emptyAction = action;
    }

    private string PeriodLabel() => CompletedPeriodDays switch
    {
        null => "すべての期間",
        1 => "今日",
        { } days => string.Create(CultureInfo.InvariantCulture, $"過去{days}日"),
    };

    private string ViewLabel() => CurrentView.Kind switch
    {
        ViewKind.Project or ViewKind.Tag or ViewKind.SavedFilter => Title,
        _ => BuiltInViews.TitleOf(CurrentView.Kind) + "ビュー",
    };
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Palette;

/// <summary>
/// コマンドパレット（S-07、UI 設計書 11、F-131〜F-138）。担当: 波3-J。View の型は持たない。
///
/// 決めごと:
/// - 入力は絞り込み記号（&gt; # @ / + ?）と検索語に分ける（<see cref="PaletteQuery"/>）。記号なしならタスク → コマンド → 移動の順に
///   各3件まで出し、超えた分は「さらに N 件」（押すとそのセクションを広げる）。記号ありならその種類だけを出す
/// - タスクは入力が 100ms 止まってから DB で探す（IME の変換中は探さない。探している間に入力が変わったら結果を捨てる）
/// - 並び: タスクは 完了済みを最後 → 一致の強さ → 期限の近い順。コマンドは 一致の強さ → 直近の使用頻度 → 定義順。
///   移動は 一致の強さ → プロジェクト・タグ・ビューの順 → 使用頻度 → 定義順（一致の弱いあいまい一致が上に来ないよう強さを先にする）
/// - 使用頻度（F-138）は実行した行の鍵を直近 100 回ぶん AppState に持ち、回数で数える（古いものから抜けるので「直近」になる）
/// - 入力が空なら「最近開いたタスク」5件（パレットから開いたもの）と「よく使うコマンド」3件
/// - Space で完了を切り替えるのは ↑↓ で行を選んだ後だけ（文字を打った直後の Space は空白として入れる）
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    public const int SearchDebounceMs = 100;
    public const int SectionLimit = 3;
    public const int ExpandedLimit = 50;
    public const int RecentShown = 5;
    public const int RecentKept = 20;
    public const int FrequentShown = 3;
    public const int UsageKept = 100;

    private const string TasksSection = "tasks";
    private const string CommandsSection = "commands";
    private const string MovesSection = "moves";

    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projectRepository;
    private readonly ITagRepository _tagRepository;
    private readonly ITemplateRepository _templates;
    private readonly IAppStateRepository _appState;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly UndoService _undo;
    private readonly QuickInputParser _parser;
    private readonly TemplateExpander _expander;
    private readonly IPaletteHost _host;
    private readonly ILogger<CommandPaletteViewModel> _logger;
    private readonly Debouncer _search;
    private readonly HashSet<string> _expanded = [];

    private IReadOnlyList<PaletteTarget> _targets = [];
    private Dictionary<Guid, Project> _projects = [];
    private List<TaskItem> _recentTasks = [];
    private List<Guid> _recentIds = [];
    private List<string> _usageLog = [];
    private Dictionary<string, int> _usage = [];
    private int _version;
    private long _typedAt;
    private bool _busy;
    private bool _initialized;

    /// <summary>文字が変わってから、まだ結果を作り直していない（入力停止を待っている）。</summary>
    private bool _stale;

    public CommandPaletteViewModel(
        ITaskRepository tasks,
        IProjectRepository projects,
        ITagRepository tags,
        ITemplateRepository templates,
        IAppStateRepository appState,
        ISettingsStore settings,
        IClock clock,
        UndoService undo,
        QuickInputParser parser,
        TemplateExpander expander,
        IPaletteHost host,
        ILogger<CommandPaletteViewModel> logger)
    {
        _tasks = tasks;
        _projectRepository = projects;
        _tagRepository = tags;
        _templates = templates;
        _appState = appState;
        _settings = settings;
        _clock = clock;
        _undo = undo;
        _parser = parser;
        _expander = expander;
        _host = host;
        _logger = logger;
        _search = new Debouncer(() => RefreshAsync());
    }

    /// <summary>
    /// 結果の行（見出し・選べる行・案内）。読み直すたびに丸ごと入れ替える。
    /// ponytail: 行は多くて 60 ほど（セクションごとに 50 件で打ち切る）なので、画面側は仮想化せずに全部並べる。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private IReadOnlyList<PaletteRow> _rows = [];

    /// <summary>読み込み済みで、出すものが1行も無い（「一致するものはありません」）。</summary>
    public bool HasNoResults => _initialized && Rows.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskSelected))]
    [NotifyPropertyChangedFor(nameof(CanNarrow))]
    [NotifyPropertyChangedFor(nameof(CanCompleteWithSpace))]
    [NotifyPropertyChangedFor(nameof(EnterHint))]
    private PaletteEntry? _selected;

    /// <summary>Tab で絞り込んだプロジェクト／タグ（null なら絞り込みなし）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScope))]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(IsInputEmpty))]
    [NotifyPropertyChangedFor(nameof(EscHint))]
    private PaletteScope? _scope;

    /// <summary>フッタの右に出す知らせ（「〇〇を完了しました」）。文字を打つと消える。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private string _notice = "";

    /// <summary>フッタの右の件数（「3 件のタスク・2 件のコマンド」）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private string _summary = "";

    /// <summary>入力欄の文字（IME の変換中の文字も含む）。</summary>
    public string Text { get; private set; } = "";

    /// <summary>↑↓ で行を選んだ後か（このときだけ Space で完了を切り替える）。文字を打つと戻る。</summary>
    public bool IsNavigating { get; private set; }

    public bool HasScope => Scope is not null;

    public bool IsInputEmpty => Text.Length == 0 && Scope is null;

    public bool IsTaskSelected => Selected is PaletteTaskItem;

    public bool CanNarrow => Selected is PaletteItem { Kind: PaletteItemKind.Project or PaletteItemKind.Tag };

    public bool CanCompleteWithSpace => IsNavigating && Selected is PaletteTaskItem;

    public string EnterHint => Selected is PaletteTaskItem ? "開く" : "実行";

    /// <summary>Esc の1回目は入力を消し、空なら閉じる。</summary>
    public string EscHint => IsInputEmpty ? "閉じる" : "消す";

    /// <summary>入力した文字でタスクを作れるか（記号なしの入力か、Tab で絞り込んだ中）。</summary>
    public bool CanCreate => Text.Trim().Length > 0 && (Scope is not null || PaletteQuery.Parse(Text).Mode == PaletteMode.All);

    public string FooterText => Notice.Length > 0 ? Notice : Summary;

    /// <summary>パレットを閉じてほしい（実行した・Esc）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>入力欄の文字を差し替えてほしい（Esc で消す・記号を入れる・Tab で絞り込む）。</summary>
    public event EventHandler<string>? TextReplaced;

    /// <summary>未完了のサブタスクがある親を完了にするときの確認（F-024）。View が MessageBox を出す。</summary>
    public Func<ConfirmRequest, ConfirmChoice> Confirm { get; set; } = _ => ConfirmChoice.No;

    /// <summary>開いたときに1回。コマンド・移動先・テンプレート・使用頻度・最近開いたタスクを読み、入力が空の結果を出す。</summary>
    public async Task InitializeAsync()
    {
        var projects = await _projectRepository.GetAllAsync();
        var tags = await _tagRepository.GetAllAsync();
        var templates = await _templates.GetAllAsync();
        var counts = await _tasks.GetViewCountsAsync();
        _projects = projects.ToDictionary(p => p.Id);
        _targets = PaletteCatalog.Build(projects, tags, _settings.Current.SavedFilters, templates, counts);

        _usageLog = ReadJson<List<string>>(await _appState.GetAsync(AppStateKeys.PaletteUsage), AppStateKeys.PaletteUsage) ?? [];
        CountUsage();
        _recentIds = ReadJson<List<Guid>>(await _appState.GetAsync(AppStateKeys.RecentTaskIds), AppStateKeys.RecentTaskIds) ?? [];
        if (_recentIds.Count > 0)
        {
            var found = (await _tasks.GetByIdsAsync(_recentIds)).Where(t => !t.IsDeleted).ToDictionary(t => t.Id);
            _recentTasks = [.. _recentIds.Where(found.ContainsKey).Select(id => found[id])];
        }
        _initialized = true;
        await RefreshAsync();
    }

    /// <summary>入力欄の文字が変わった（View から）。IME の変換中は探さず、確定して 100ms 止まったら探す。</summary>
    public void OnTextChanged(string text, bool isComposing)
    {
        SetText(text);
        if (!isComposing)
        {
            _typedAt = Stopwatch.GetTimestamp();
            _search.Schedule(SearchDebounceMs);
        }
    }

    /// <summary>文字を入れる（探すのは <see cref="RefreshAsync"/>）。広げたセクション・知らせ・↑↓ の状態は戻す。</summary>
    public void SetText(string text)
    {
        if (Text == text)
        {
            return;
        }
        Text = text;
        IsNavigating = false;
        Notice = "";
        _stale = true;
        _expanded.Clear();
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(IsInputEmpty));
        OnPropertyChanged(nameof(EscHint));
        OnPropertyChanged(nameof(CanCompleteWithSpace));
    }

    /// <summary>
    /// 入力停止を待っている間に Enter・Tab が来たら、先に今の文字で結果を作り直す
    /// （速く打って Enter を押したとき、打つ前の結果の先頭を実行してしまわないように）。
    /// </summary>
    public async Task EnsureFreshAsync()
    {
        if (_stale)
        {
            await RefreshAsync();
        }
    }

    /// <summary>今の入力で結果を作り直す。keepKey があればその行を、無ければ keepIndex 番目（無ければ先頭）を選ぶ。</summary>
    public async Task RefreshAsync(string? keepKey = null, int? keepIndex = null)
    {
        var version = ++_version;
        var query = Scope is null ? PaletteQuery.Parse(Text) : new PaletteQuery(PaletteMode.All, Text.Trim());
        var watch = Stopwatch.StartNew();
        IReadOnlyList<TaskListRow> found = [];
        if (Scope is not null || (query.Mode == PaletteMode.All && query.HasTerm))
        {
            found = await _tasks.QueryAsync(TaskQueryFor(query, Scope));
            if (version != _version)
            {
                return;   // 探している間に入力が変わった（新しい方の結果を待つ）
            }
        }
        var queryMs = watch.ElapsedMilliseconds;
        var rows = BuildRows(query, found);
        _stale = false;
        Rows = rows;
        var entries = rows.OfType<PaletteEntry>().ToList();
        Selected = entries.FirstOrDefault(e => keepKey is not null && e.Key == keepKey)
            ?? (keepIndex is { } index && entries.Count > 0 ? entries[Math.Clamp(index, 0, entries.Count - 1)] : entries.FirstOrDefault());
        if (_typedAt != 0)
        {
            _logger.LogDebug("パレットの検索 {QueryMs} ms・入力から表示まで {TotalMs} ms（タスク {Count} 件）",
                queryMs, (long)Stopwatch.GetElapsedTime(_typedAt).TotalMilliseconds, found.Count);
            _typedAt = 0;
        }
    }

    /// <summary>↑↓（セクションをまたいで動く。端で止まる）。</summary>
    public void MoveSelection(int delta)
    {
        var entries = Rows.OfType<PaletteEntry>().ToList();
        if (entries.Count == 0)
        {
            return;
        }
        var index = Selected is null ? -1 : entries.IndexOf(Selected);
        Selected = entries[Math.Clamp(index + delta, 0, entries.Count - 1)];
        IsNavigating = true;
        OnPropertyChanged(nameof(CanCompleteWithSpace));
    }

    /// <summary>Esc の1回目: 入力（と絞り込み）があれば消して true。無ければ false（閉じる）。</summary>
    public async Task<bool> ClearInputAsync()
    {
        if (IsInputEmpty)
        {
            return false;
        }
        Scope = null;
        ReplaceText("");
        await RefreshAsync();
        return true;
    }

    /// <summary>入力が空のときの Backspace: 絞り込みを外す。外したら true。</summary>
    public async Task<bool> RemoveScopeAsync()
    {
        if (Scope is null)
        {
            return false;
        }
        Scope = null;
        await RefreshAsync();
        return true;
    }

    /// <summary>Tab: 選んでいるプロジェクト／タグの中で探し続ける（UI 設計書 11.5）。絞り込めたら true。</summary>
    public async Task<bool> NarrowAsync()
    {
        await EnsureFreshAsync();
        if (Selected is not PaletteItem { Kind: PaletteItemKind.Project or PaletteItemKind.Tag } item || item.Target.EntityId is not { } id)
        {
            return false;
        }
        await RecordUsageAsync(item.Key);
        Scope = new PaletteScope(item.Kind, id, item.Target.Name, item.Target.ColorHex);
        ReplaceText("");
        await RefreshAsync();
        return true;
    }

    partial void OnSelectedChanged(PaletteEntry? oldValue, PaletteEntry? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }
        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    // ---- 結果を組み立てる ----

    private static TaskQuery TaskQueryFor(PaletteQuery query, PaletteScope? scope) => new()
    {
        Status = TaskStatusFilter.All,
        SearchText = query.HasTerm ? query.Term : null,
        ProjectId = scope is { IsProject: true } ? scope.Id : null,
        TagIds = scope is { IsProject: false } ? [scope.Id] : [],
        SortKey = TaskSortKey.Due,
        IncludeSubtasks = false,
    };

    private sealed record Section(string Key, string Title, IReadOnlyList<PaletteEntry> Items, int Total, bool CanExpand);

    private List<PaletteRow> BuildRows(PaletteQuery query, IReadOnlyList<TaskListRow> found)
    {
        var sections = new List<Section>();
        var terms = query.Terms;
        var create = CreateTarget();
        string summary;
        if (Scope is { } scope)
        {
            sections.Add(Limit(TasksSection, $"「{scope.Label}」のタスク", RankTasks(found, terms), mixed: false, found.Count));
            if (create is not null)
            {
                sections.Add(Limit(CommandsSection, "コマンド", [Plain(create)], mixed: false));
            }
            summary = Count(found.Count);
        }
        else if (query.Mode == PaletteMode.All && !query.HasTerm)
        {
            var recent = _recentTasks.Take(RecentShown).Select(t => CreateTaskItem(t, [])).ToList<PaletteEntry>();
            sections.Add(new Section("recent", "最近開いたタスク", recent, recent.Count, CanExpand: false));
            var frequent = Frequent();
            sections.Add(new Section("frequent", "よく使うコマンド", frequent, frequent.Count, CanExpand: false));
            summary = "";
        }
        else if (query.Mode == PaletteMode.All)
        {
            var commands = Rank(t => t.Kind is PaletteItemKind.Command or PaletteItemKind.Template, terms, byKind: false);
            var moves = Rank(t => t.Kind is PaletteItemKind.Project or PaletteItemKind.Tag or PaletteItemKind.View or PaletteItemKind.SavedFilter, terms, byKind: true);
            sections.Add(Limit(TasksSection, "タスク", RankTasks(found, terms), mixed: true, found.Count));
            sections.Add(Limit(CommandsSection, "コマンド", create is null ? commands : [Plain(create), .. commands], mixed: true));
            sections.Add(Limit(MovesSection, "移動", moves, mixed: true));
            summary = string.Join("・", new[]
            {
                found.Count > 0 ? $"{found.Count} 件のタスク" : null,
                commands.Count > 0 ? $"{commands.Count} 件のコマンド" : null,
                moves.Count > 0 ? $"{moves.Count} 件の移動" : null,
            }.OfType<string>());
        }
        else
        {
            var (title, items) = query.Mode switch
            {
                PaletteMode.Commands => ("コマンド", Rank(t => t.Kind is PaletteItemKind.Command or PaletteItemKind.Template, terms, byKind: false)),
                PaletteMode.Tags => ("タグ", Rank(t => t.Kind == PaletteItemKind.Tag, terms, byKind: false)),
                PaletteMode.Projects => ("プロジェクト", Rank(t => t.Kind == PaletteItemKind.Project, terms, byKind: false)),
                PaletteMode.Views => ("ビュー", Rank(t => t.Kind is PaletteItemKind.View or PaletteItemKind.SavedFilter, terms, byKind: true)),
                PaletteMode.Templates => ("テンプレート", Rank(t => t.Kind == PaletteItemKind.Template, terms, byKind: false)),
                _ => ("ヘルプ", Rank(t => t.Kind == PaletteItemKind.Prefix || t.Command == PaletteCommand.Shortcuts, terms, byKind: false, keepOrder: true)),
            };
            sections.Add(Limit(query.Mode.ToString().ToLowerInvariant(), title, items, mixed: false));
            summary = Count(items.Count);
        }
        Summary = summary;

        var rows = new List<PaletteRow>();
        foreach (var section in sections.Where(s => s.Items.Count > 0))
        {
            rows.Add(new PaletteHeaderRow(section.Title, hasDivider: rows.Count > 0));
            rows.AddRange(section.Items);
            var rest = section.Total - section.Items.Count;
            if (rest <= 0)
            {
                continue;
            }
            rows.Add(section.CanExpand
                ? new PaletteItem(
                    new PaletteTarget(PaletteItemKind.More, "more:" + section.Key, $"さらに {rest} 件", int.MaxValue) { Parameter = section.Key },
                    [new TextPiece($"さらに {rest} 件", false)])
                : new PaletteNoteRow($"ほか {rest} 件。語を足すと絞り込めます"));
        }
        return rows;
    }

    /// <summary>
    /// 記号なしの入力では各セクション3件まで（広げたら 50 件）。記号あり・絞り込み中は 50 件まで。
    /// total は当たった全件（タスクは 50 件より先を行にしないので別に渡す）。
    /// </summary>
    private Section Limit(string key, string title, IReadOnlyList<PaletteEntry> items, bool mixed, int? total = null)
    {
        var limit = mixed && !_expanded.Contains(key) ? SectionLimit : ExpandedLimit;
        return new Section(key, title, [.. items.Take(limit)], total ?? items.Count, CanExpand: limit < ExpandedLimit);
    }

    private List<PaletteEntry> Rank(Func<PaletteTarget, bool> filter, IReadOnlyList<string> terms, bool byKind, bool keepOrder = false) =>
    [
        .. _targets
            .Where(filter)
            .Select(t => (Target: t, Match: PaletteText.Match(t.Name, terms, t.Keywords)))
            .Where(x => x.Match.IsMatch)
            .OrderByDescending(x => keepOrder ? 0 : x.Match.Strength)
            .ThenBy(x => byKind ? KindOrder(x.Target.Kind) : 0)
            .ThenByDescending(x => keepOrder ? 0 : _usage.GetValueOrDefault(x.Target.Key))
            .ThenBy(x => x.Target.Order)
            .Select(x => new PaletteItem(x.Target, PaletteText.Pieces(x.Target.Label, x.Match.Spans, x.Target.LabelPrefix.Length))),
    ];

    private static int KindOrder(PaletteItemKind kind) => kind switch
    {
        PaletteItemKind.Project => 0,
        PaletteItemKind.Tag => 1,
        PaletteItemKind.View => 2,
        _ => 3,
    };

    /// <summary>タスクの並び（UI 設計書 11.4）: 完了済みは最後 → 一致の強さ（前方 ＞ 部分 ＞ メモだけ）→ 期限の近い順（期限なしは後ろ）。</summary>
    private List<PaletteEntry> RankTasks(IReadOnlyList<TaskListRow> rows, IReadOnlyList<string> terms) =>
    [
        .. rows
            .Select(r => (r.Task, Match: PaletteText.MatchTitle(r.Task.Title, terms)))
            .OrderBy(x => !x.Task.IsOpen)
            .ThenByDescending(x => x.Match.Strength)
            .ThenBy(x => x.Task.DueAt is null)
            .ThenBy(x => x.Task.DueAt)
            .ThenBy(x => x.Task.Id)
            .Take(ExpandedLimit)
            .Select(x => CreateTaskItem(x.Task, x.Match.Spans)),
    ];

    /// <summary>入力が空のときの「よく使うコマンド」: 使った回数の多い順に3件。足りなければコマンドを定義順で足す。</summary>
    private List<PaletteEntry> Frequent()
    {
        var used = _targets
            .Where(t => t.Kind is not (PaletteItemKind.Prefix or PaletteItemKind.More) && _usage.GetValueOrDefault(t.Key) > 0)
            .OrderByDescending(t => _usage[t.Key])
            .ThenBy(t => t.Order)
            .Take(FrequentShown)
            .ToList();
        var fill = _targets.Where(t => t.Kind == PaletteItemKind.Command && !used.Contains(t)).Take(FrequentShown - used.Count);
        return [.. used.Concat(fill).Select(Plain)];
    }

    private static PaletteItem Plain(PaletteTarget target) => new(target, [new TextPiece(target.Label, false)]);

    /// <summary>「〇〇」という名前でタスクを追加（入力をクイック入力と同じ規則で読む。右に読めた期限・タグなどを出す）。</summary>
    private PaletteTarget? CreateTarget()
    {
        if (!CanCreate)
        {
            return null;
        }
        var parsed = _parser.Parse(Text.Trim());
        var parts = new List<string>();
        if (parsed.DueAt is { } due)
        {
            parts.Add(DisplayText.DueShort(due, parsed.DueHasTime, _clock));
        }
        if (parsed.RRule is not null)
        {
            parts.Add("繰り返し");
        }
        if (parsed.ProjectName is { } project)
        {
            parts.Add("@" + project);
        }
        parts.AddRange(parsed.TagNames.Select(t => "#" + t));
        if (parsed.Priority != Priority.None)
        {
            parts.Add(DisplayText.PrioritySymbol(parsed.Priority));
        }
        return new PaletteTarget(PaletteItemKind.CreateTask, "create", parsed.Title, -1)
        {
            LabelPrefix = "「",
            LabelSuffix = "」という名前でタスクを追加",
            IconKey = "IconAdd",
            KeyHint = "Ctrl+Enter",
            Detail = parts.Count > 0 ? string.Join(" ", parts) : null,
        };
    }

    private PaletteTaskItem CreateTaskItem(TaskItem task, IReadOnlyList<(int Start, int Length)> spans)
    {
        var project = task.ProjectId is { } projectId && _projects.TryGetValue(projectId, out var p) ? p : null;
        var overdue = TaskRules.IsOverdue(task, _clock);
        var today = _clock.LocalToday();
        var due = "";
        if (!task.IsOpen)
        {
            due = task.CompletedAt is { } closed ? MonthDay(_clock.ToLocalDate(closed)) : "";
        }
        else if (task.DueAt is { } at)
        {
            due = overdue && _clock.ToLocalDate(at) < today
                ? DisplayText.DueColumn(task, _clock).Text
                : DisplayText.DueShort(at, task.DueHasTime, _clock);
        }
        return new PaletteTaskItem
        {
            Task = task,
            Pieces = PaletteText.Pieces(task.Title, spans),
            ProjectName = project?.Name,
            DueText = due,
            IsOverdue = overdue,
            IsDueStrong = task.IsOpen && task.DueHasTime && task.DueAt is { } d && _clock.ToLocalDate(d) == today,
            IsClosed = !task.IsOpen,
        };
    }

    private static string MonthDay(DateOnly day) => string.Create(CultureInfo.InvariantCulture, $"{day.Month}/{day.Day}");

    private static string Count(int count) => count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{count} 件");

    private void ReplaceText(string text)
    {
        SetText(text);
        TextReplaced?.Invoke(this, text);
    }

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ---- 使用頻度と最近開いたタスク（AppState に JSON で持つ） ----

    private void CountUsage() =>
        _usage = _usageLog.GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private async Task RecordUsageAsync(string key)
    {
        _usageLog.Add(key);
        if (_usageLog.Count > UsageKept)
        {
            _usageLog.RemoveRange(0, _usageLog.Count - UsageKept);
        }
        CountUsage();
        await _appState.SetAsync(AppStateKeys.PaletteUsage, JsonSerializer.Serialize(_usageLog));
    }

    private async Task RememberRecentAsync(Guid taskId)
    {
        _recentIds.Remove(taskId);
        _recentIds.Insert(0, taskId);
        if (_recentIds.Count > RecentKept)
        {
            _recentIds.RemoveRange(RecentKept, _recentIds.Count - RecentKept);
        }
        await _appState.SetAsync(AppStateKeys.RecentTaskIds, JsonSerializer.Serialize(_recentIds));
    }

    /// <summary>壊れた JSON は捨てて空から始める（記録が消えるだけで、パレットは使える）。</summary>
    private T? ReadJson<T>(string? json, string key) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "パレットの記録が読めないので空から始めます: {Key}", key);
            return null;
        }
    }
}

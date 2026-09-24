using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Controls.Pickers;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Scratch;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Results;
using TaskDeck.Core.Scratch;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Templates;

/// <summary>右ペインに何を出しているか。</summary>
public enum TemplatesMode
{
    /// <summary>テンプレートが1つも無い（または読めなかった）。</summary>
    Empty,
    /// <summary>選んだテンプレートの展開プレビュー。</summary>
    Preview,
    /// <summary>新規作成・編集。</summary>
    Editor,
    /// <summary>「選択中のタスクから作る」の名前と基準日を聞いているところ。</summary>
    FromTasks,
}

/// <summary>左の一覧の1行。Group は「よく使う」「すべて」。</summary>
public sealed record TemplateListItem(
    Guid Id,
    string Name,
    string? IconKey,
    string? ColorHex,
    int ItemCount,
    int UseCount,
    string Summary,
    string? UseCountText,
    string Group);

/// <summary>展開プレビューの1行（UI 設計書 20.4）。チェックを外すとその項目と子孫を作らない。</summary>
public sealed partial class ExpansionRow(Guid itemId, string title, int level, Priority priority, ExpansionRow? parent) : ObservableObject
{
    public Guid ItemId { get; } = itemId;

    public string Title { get; } = title;

    public int Level { get; } = level;

    public ExpansionRow? Parent { get; } = parent;

    public Priority Priority { get; } = priority;

    public string PriorityMarks { get; } = Marks(priority);

    public bool IsSubtask => Level > 0;

    /// <summary>字下げ（UI 設計書 20.4: サブタスクは左に32px。行の余白6pxを除いた分）。</summary>
    public double Indent => Level * 26;

    /// <summary>この行のチェック（利用者が外したか）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    private bool _isIncluded = true;

    /// <summary>親（か祖先）が外されている。このときは自分も作られず、チェックは触れない。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    private bool _isParentExcluded;

    /// <summary>「3日前」。期限なしは null。</summary>
    [ObservableProperty]
    private string? _offsetText;

    /// <summary>「9/18（木）過去」「9/22（火）今日」「9/24（木） 20:00」。期限なしは null。</summary>
    [ObservableProperty]
    private string? _dateText;

    [ObservableProperty]
    private bool _isToday;

    [ObservableProperty]
    private bool _isPast;

    /// <summary>画面のチェック（親が外されていれば外れて見える）。</summary>
    public bool IsChecked
    {
        get => IsIncluded && !IsParentExcluded;
        set => IsIncluded = value;
    }

    internal static string Marks(Priority priority) => priority switch
    {
        Priority.Low => "!",
        Priority.Medium => "!!",
        Priority.High => "!!!",
        Priority.Urgent => "!!!!",
        _ => "",
    };
}

/// <summary>タグの選択肢1つ。</summary>
public sealed partial class TagChoice(Guid id, string name) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Label => "#" + Name;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>タグを選ぶ欄（チェックの一覧と「#仕事 #買い物」の要約）。選び直すと Changed が出る。</summary>
public sealed partial class TagSelection : ObservableObject
{
    private bool _resetting;

    public ObservableCollection<TagChoice> Choices { get; } = [];

    public event EventHandler? Changed;

    public bool IsEmpty => Choices.Count == 0;

    public IReadOnlyList<Guid> SelectedIds => [.. Choices.Where(c => c.IsSelected).Select(c => c.Id)];

    /// <summary>「#仕事 #買い物」。何も選んでいなければ「なし」。</summary>
    public string Summary
    {
        get
        {
            var names = Choices.Where(c => c.IsSelected).Select(c => c.Label).ToList();
            return names.Count == 0 ? "なし" : string.Join(' ', names);
        }
    }

    public void Reset(IReadOnlyList<Tag> tags, IEnumerable<Guid> selected)
    {
        _resetting = true;
        var chosen = selected.ToHashSet();
        foreach (var choice in Choices)
        {
            choice.PropertyChanged -= OnChoiceChanged;
        }
        Choices.Clear();
        foreach (var tag in tags)
        {
            var choice = new TagChoice(tag.Id, tag.Name) { IsSelected = chosen.Contains(tag.Id) };
            choice.PropertyChanged += OnChoiceChanged;
            Choices.Add(choice);
        }
        _resetting = false;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_resetting)
        {
            return;
        }
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>テンプレート項目を木の順（親の直後に子、兄弟は SortOrder 順）に並べる。</summary>
internal static class TemplateTree
{
    public static IEnumerable<(TaskTemplateItem Item, int Level)> Flatten(IReadOnlyList<TaskTemplateItem> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        var children = items.ToLookup(i => i.ParentItemId is { } p && p != i.Id && ids.Contains(p) ? p : Guid.Empty);
        return Visit(Guid.Empty, 0);

        IEnumerable<(TaskTemplateItem, int)> Visit(Guid parentId, int level)
        {
            foreach (var item in children[parentId].OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
            {
                yield return (item, level);
                if (level < TaskItem.MaxDepth)
                {
                    foreach (var child in Visit(item.Id, level + 1))
                    {
                        yield return child;
                    }
                }
            }
        }
    }
}

/// <summary>
/// テンプレート画面（S-11、UI 設計書 20章）。左に一覧、右に展開プレビュー／編集／「選択中のタスクから作る」。
/// 展開は TemplateExpander.Plan で計画 → ITemplateRepository.ExpandAsync → UndoService.Record（トーストはメイン画面が出す）。
/// 読み書きの失敗は Notice に短く出してログに残す（ログにテンプレート名・タスク名は出さない）。
/// </summary>
public sealed partial class TemplatesViewModel : ObservableObject
{
    public const string FrequentGroup = "よく使う";
    public const string AllGroup = "すべて";

    private readonly ITemplateRepository _templates;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly ITagRepository _tags;
    private readonly TemplateExpander _expander;
    private readonly UndoService _undo;
    private readonly ShellService _shell;
    private readonly IClock _clock;
    private readonly ScratchPresenter _scratch;
    private readonly ILogger<TemplatesViewModel> _logger;

    /// <summary>プロジェクトの名前と色（編集画面とも共有する。読み直しても同じ入れ物を使う）。</summary>
    private readonly Dictionary<Guid, Project> _projectById = [];
    private List<TemplateSummary> _all = [];
    private IReadOnlyList<Tag> _allTags = [];
    private TemplateWithItems? _current;
    private IReadOnlyList<Guid> _fromTaskIds = [];
    private bool _selecting;
    private bool _suspendPlan;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private TemplateListItem? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListEnabled), nameof(CanExpand))]
    [NotifyCanExecuteChangedFor(nameof(ExpandCommand), nameof(OpenAsScratchCommand))]
    private TemplatesMode _mode;

    /// <summary>短い知らせ（読み込みの失敗・「タスクを選んでから」の案内）。</summary>
    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExpand))]
    [NotifyCanExecuteChangedFor(nameof(ExpandCommand), nameof(OpenAsScratchCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _templateName = "";

    [ObservableProperty]
    private string? _templateDescription;

    [ObservableProperty]
    private string? _lastUsedText;

    /// <summary>「基準日（出発日）」。</summary>
    [ObservableProperty]
    private string _anchorCaption = "基準日";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorDateText))]
    private DateOnly _anchorDate;

    /// <summary>まとめて入れる先（F-157）。null は「なし」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectName), nameof(ProjectColor))]
    private Guid? _projectId;

    /// <summary>true ならタグはテンプレートどおり（既定＋項目のタグ）。false なら ExpansionTags で上書き。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TagsSummary))]
    private bool _useTemplateTags = true;

    /// <summary>過去になる期限を今日に寄せる（既定 ON、UI 設計書 20.5）。</summary>
    [ObservableProperty]
    private bool _pullPastToToday = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandText), nameof(CanExpand))]
    [NotifyCanExecuteChangedFor(nameof(ExpandCommand))]
    private int _createCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPast))]
    private int _pastCount;

    [ObservableProperty]
    private string? _pastWarningText;

    [ObservableProperty]
    private string? _pullPastText;

    [ObservableProperty]
    private TemplateEditorViewModel? _editor;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private string _fromTasksName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FromTasksAnchorText))]
    private DateOnly _fromTasksAnchor;

    [ObservableProperty]
    private string? _fromTasksSummary;

    [ObservableProperty]
    private string? _fromTasksError;

    public TemplatesViewModel(
        ITemplateRepository templates,
        ITaskRepository tasks,
        IProjectRepository projects,
        ITagRepository tags,
        TemplateExpander expander,
        UndoService undo,
        ShellService shell,
        IClock clock,
        AppPaths paths,
        ScratchPresenter scratch,
        ILogger<TemplatesViewModel> logger)
    {
        _templates = templates;
        _tasks = tasks;
        _projects = projects;
        _tags = tags;
        _expander = expander;
        _undo = undo;
        _shell = shell;
        _clock = clock;
        _scratch = scratch;
        _logger = logger;
        IsDevToolsVisible = TemplatesDevTools.IsEnabled(paths);
        _anchorDate = clock.LocalToday();
        _fromTasksAnchor = _anchorDate;
        ExpansionTags.Changed += (_, _) =>
        {
            UseTemplateTags = false;
            OnPropertyChanged(nameof(TagsSummary));
            RefreshPlan();
        };
    }

    /// <summary>展開したら閉じる（「やめる」でも）。</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<TemplateListItem> Items { get; } = [];

    public ObservableCollection<ExpansionRow> Rows { get; } = [];

    /// <summary>「付けるタグ」の上書き（F-157）。</summary>
    public TagSelection ExpansionTags { get; } = new();

    /// <summary>開発用データフォルダで TASKDECK_DEV_TEMPLATES があるときだけ、確認用のボタンを出す。</summary>
    public bool IsDevToolsVisible { get; }

    /// <summary>編集中・「タスクから作る」の途中は一覧を触らせない（入力を失わないように）。</summary>
    public bool IsListEnabled => Mode is TemplatesMode.Empty or TemplatesMode.Preview;

    public string AnchorDateText => DateLabels.Long(AnchorDate, _clock.LocalToday());

    public string FromTasksAnchorText => DateLabels.Long(FromTasksAnchor, _clock.LocalToday());

    public string ProjectName => ProjectLabel(ProjectId);

    public string? ProjectColor => ProjectColorOf(ProjectId);

    public string TagsSummary => UseTemplateTags ? "テンプレートどおり" : ExpansionTags.Summary;

    public bool HasPast => PastCount > 0;

    public string ExpandText => $"{CreateCount} 件を作る";

    public bool CanExpand => Mode == TemplatesMode.Preview && CreateCount > 0 && !IsBusy;

    /// <summary>窓を開いたとき。一覧を読み、先頭（よく使う順）を選ぶ。</summary>
    public Task LoadAsync() => ReloadAsync(null);

    /// <summary>一覧で選んで、右に中身を出す（読み終わるまで待てる。一覧のクリックは OnSelectedChanged から同じ読み込みに入る）。</summary>
    public Task SelectAsync(TemplateListItem? item) => SelectByIdAsync(item?.Id);

    /// <summary>「まとめて入れる先」「既定の入れる先」を選んだ（ProjectPickerPanel の Picked）。新しく作られたプロジェクトなら読み直す。</summary>
    public async Task PickProjectAsync(Guid? projectId)
    {
        if (projectId is { } id && !_projectById.ContainsKey(id))
        {
            try
            {
                await ReloadProjectsAsync();
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                Fail(ex, "プロジェクトを読み込めませんでした");
            }
        }
        if (Mode == TemplatesMode.Editor && Editor is not null)
        {
            Editor.DefaultProjectId = projectId;
            Editor.RefreshProjectLabel();
            return;
        }
        ProjectId = projectId;
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectColor));
    }

    /// <summary>
    /// Esc の段階的な動作: 削除の確認 → 編集・「タスクから作る」をやめて戻る。戻る先が無ければ false（窓を閉じる）。
    /// </summary>
    public bool GoBack()
    {
        if (IsConfirmingDelete)
        {
            IsConfirmingDelete = false;
            return true;
        }
        if (Mode is TemplatesMode.Editor or TemplatesMode.FromTasks)
        {
            BackToPreview();
            return true;
        }
        return false;
    }

    public string ProjectLabel(Guid? projectId) =>
        projectId is { } id && _projectById.TryGetValue(id, out var project) ? project.Name : "なし";

    public string? ProjectColorOf(Guid? projectId) =>
        projectId is { } id && _projectById.TryGetValue(id, out var project) ? project.ColorHex : null;

    /// <summary>
    /// ほかの画面でテンプレートが変わった（DataChangeHub。窓が UI スレッドに戻して呼ぶ）。
    /// 一覧だけ読み直し、右側の中身と入力中の値（基準日・チェック・編集中の内容）には触らない。
    /// 出していたテンプレートが消えたときだけ、先頭を出し直す。
    /// </summary>
    public async Task RefreshListAsync()
    {
        try
        {
            _all = [.. await _templates.GetAllAsync()];
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            Fail(ex, "テンプレートを読み込めませんでした");
            return;
        }
        var shown = _all.FirstOrDefault(s => s.Template.Id == _current?.Template.Id)?.Template;
        RebuildListKeepingSelection();
        if (shown is not null)
        {
            LastUsedText = LastUsedLabel(shown);   // 使用回数と最終使用日だけは新しい値にする
            return;
        }
        if (Mode is TemplatesMode.Preview or TemplatesMode.Empty)
        {
            await SelectByIdAsync(Items.FirstOrDefault()?.Id);
        }
    }

    /// <summary>テーマが変わった。色（アイコン・プロジェクトの点）はコンバータがその時のテーマで作るので、作り直させる。</summary>
    public void RefreshThemeColors()
    {
        RebuildListKeepingSelection();
        OnPropertyChanged(nameof(ProjectColor));
        Editor?.RefreshThemeColors();
    }

    partial void OnSelectedChanged(TemplateListItem? value)
    {
        if (!_selecting && value is not null && value.Id != _current?.Template.Id)
        {
            _ = LoadDetailAsync(value.Id);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildList();
        var shown = Items.FirstOrDefault(i => i.Id == _current?.Template.Id);
        if (shown is null && Items.Count > 0)
        {
            // 絞り込みで見えなくなったら、見えている先頭を出す（見えないテンプレートを展開しないように）
            Selected = Items[0];
            return;
        }
        _selecting = true;
        try
        {
            Selected = shown;
        }
        finally
        {
            _selecting = false;
        }
    }

    partial void OnAnchorDateChanged(DateOnly value) => RefreshPlan();

    partial void OnProjectIdChanged(Guid? value) => RefreshPlan();

    partial void OnPullPastToTodayChanged(bool value) => RefreshPlan();

    [RelayCommand(CanExecute = nameof(CanExpand))]
    private async Task ExpandAsync()
    {
        if (_current is null)
        {
            return;
        }
        var plan = BuildPlan();
        if (plan.Tasks.Count == 0)
        {
            return;
        }
        var templateId = _current.Template.Id;
        IsBusy = true;
        try
        {
            var result = await _templates.ExpandAsync(plan);
            _undo.Record(result, $"「{_current.Template.Name}」から {plan.Tasks.Count} 件を作りました", UndoKind.Bulk, showToast: true);
            _logger.LogInformation("テンプレート {TemplateId} から {Count} 件を作りました", templateId, plan.Tasks.Count);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            Fail(ex, "タスクを作れませんでした", templateId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 「使い捨てで開く」: 出しているテンプレートの項目（題名と階層だけ）から使い捨てリストを作り、この画面を閉じて
    /// 最後に使った方（メイン画面か小窓か）で見せる。タスクは作らない（「N 件を作る」とは別）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenAsScratch))]
    private async Task OpenAsScratchAsync()
    {
        if (_current is null)
        {
            return;
        }
        var templateId = _current.Template.Id;
        ScratchList? list;
        IsBusy = true;
        try
        {
            list = await _scratch.CreateFromTemplateAsync(templateId);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            Fail(ex, "使い捨てリストを作れませんでした", templateId);
            return;
        }
        finally
        {
            IsBusy = false;
        }
        if (list is null)
        {
            Notice = "テンプレートが見つかりません（削除された可能性があります）";
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _scratch.Open(list.Id);
    }

    private bool CanOpenAsScratch() => Mode == TemplatesMode.Preview && _current is not null && !IsBusy;

    /// <summary>「新しい使い捨てリスト」: 空のリストを作り、この画面を閉じて最後に使った方で見せる。</summary>
    [RelayCommand]
    private void NewScratch()
    {
        var list = _scratch.CreateNew();
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _scratch.Open(list.Id);
    }

    /// <summary>「付けるタグ」をテンプレートどおりに戻す。</summary>
    [RelayCommand]
    private void ResetTags()
    {
        if (_current is null)
        {
            return;
        }
        ExpansionTags.Reset(_allTags, _current.Template.DefaultTagIds);
        UseTemplateTags = true;
        RefreshPlan();
    }

    [RelayCommand]
    private void NewTemplate()
    {
        Notice = null;
        Editor = new TemplateEditorViewModel(null, [], _allTags, _projectById);
        IsConfirmingDelete = false;
        Mode = TemplatesMode.Editor;
    }

    [RelayCommand]
    private void Edit()
    {
        if (_current is null)
        {
            return;
        }
        Notice = null;
        Editor = new TemplateEditorViewModel(_current.Template, _current.Items, _allTags, _projectById);
        IsConfirmingDelete = false;
        Mode = TemplatesMode.Editor;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is not { } editor || !editor.TryBuild(out var template, out var items))
        {
            return;
        }
        IsBusy = true;
        try
        {
            var saved = await _templates.SaveAsync(template, items);
            _logger.LogInformation("テンプレート {TemplateId} を保存しました（項目 {Count} 件）", saved.Id, items.Count);
            Editor = null;
            await ReloadAsync(saved.Id);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            editor.Error = "保存できませんでした（ログに記録しました）";
            _logger.LogError(ex, "テンプレート {TemplateId} を保存できませんでした", template.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelEdit() => BackToPreview();

    /// <summary>削除は取り消せないので、まず確認を出す（UI 設計書 13.2）。</summary>
    [RelayCommand]
    private void RequestDelete() => IsConfirmingDelete = Editor is { IsNew: false };

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (_current is null)
        {
            return;
        }
        var templateId = _current.Template.Id;
        IsConfirmingDelete = false;
        try
        {
            await _templates.DeleteAsync(templateId);
            _logger.LogInformation("テンプレート {TemplateId} を削除しました", templateId);
            Editor = null;
            _current = null;
            await ReloadAsync(null);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            Fail(ex, "削除できませんでした", templateId);
        }
    }

    /// <summary>「選択中のタスクから作る」（F-152）。選択が無ければ案内だけ出す。</summary>
    [RelayCommand]
    private async Task StartFromTasksAsync()
    {
        Notice = null;
        var ids = _shell.SelectedTaskIds;
        if (ids.Count == 0)
        {
            Notice = "メイン画面でタスクを選んでから押してください";
            return;
        }
        IReadOnlyList<TaskItem> found;
        try
        {
            found = await _tasks.GetByIdsAsync(ids);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            Fail(ex, "選んだタスクを読み込めませんでした");
            return;
        }
        var byId = found.Where(t => !t.IsDeleted).ToDictionary(t => t.Id);
        var ordered = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (ordered.Count == 0)
        {
            Notice = "選んだタスクが見つかりません（削除された可能性があります）";
            return;
        }
        _fromTaskIds = [.. ordered.Select(t => t.Id)];
        FromTasksName = ordered[0].Title;
        FromTasksSummary = ordered.Count == 1
            ? $"「{ordered[0].Title}」とそのサブタスクをテンプレートにします。"
            : $"「{ordered[0].Title}」ほか {ordered.Count - 1} 件（サブタスクを含む）をテンプレートにします。";
        var dues = ordered.Where(t => t.DueAt is not null).Select(t => _clock.ToLocalDate(t.DueAt!.Value)).ToList();
        FromTasksAnchor = dues.Count > 0 ? dues.Min() : _clock.LocalToday();
        FromTasksError = null;
        IsConfirmingDelete = false;
        Mode = TemplatesMode.FromTasks;
    }

    [RelayCommand]
    private async Task CreateFromTasksAsync()
    {
        FromTasksError = null;
        OperationResult<TaskTemplate> result;
        IsBusy = true;
        try
        {
            result = await _templates.CreateFromTasksAsync(FromTasksName, _fromTaskIds, FromTasksAnchor);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            FromTasksError = "テンプレートを作れませんでした（ログに記録しました）";
            _logger.LogError(ex, "選択中のタスク {Count} 件からテンプレートを作れませんでした", _fromTaskIds.Count);
            return;
        }
        finally
        {
            IsBusy = false;
        }
        if (!result.Succeeded || result.Value is not { } created)
        {
            FromTasksError = result.Error;
            return;
        }
        _logger.LogInformation("選択中のタスク {Count} 件からテンプレート {TemplateId} を作りました", _fromTaskIds.Count, created.Id);
        await ReloadAsync(created.Id);
        if (_current?.Template.Id == created.Id)
        {
            Edit();   // 作ったテンプレートをそのまま手直しできるように
        }
    }

    [RelayCommand]
    private void CancelFromTasks() => BackToPreview();

    /// <summary>確認用（開発時だけ出るボタン）: 「すべて」の先頭2件を、メイン画面で選んだことにする。</summary>
    [RelayCommand]
    private async Task DevSelectSampleTasksAsync()
    {
        var rows = await _tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.All));
        _shell.SelectedTaskIds = [.. rows.Where(r => r.Task.ParentTaskId is null).Take(2).Select(r => r.Task.Id)];
        Notice = $"確認用: メイン画面で {_shell.SelectedTaskIds.Count} 件を選んだことにしました";
    }

    private void BackToPreview()
    {
        Editor = null;
        IsConfirmingDelete = false;
        FromTasksError = null;
        Mode = _current is null ? TemplatesMode.Empty : TemplatesMode.Preview;
    }

    private async Task ReloadAsync(Guid? selectId)
    {
        try
        {
            _all = [.. await _templates.GetAllAsync()];
            _allTags = await _tags.GetAllAsync();
            await ReloadProjectsAsync();
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            Fail(ex, "テンプレートを読み込めませんでした");
            return;
        }
        RebuildList();
        await SelectByIdAsync(selectId ?? Items.FirstOrDefault()?.Id);
    }

    private async Task SelectByIdAsync(Guid? templateId)
    {
        _selecting = true;
        try
        {
            Selected = Items.FirstOrDefault(i => i.Id == templateId);
        }
        finally
        {
            _selecting = false;
        }
        await LoadDetailAsync(templateId);
    }

    private async Task ReloadProjectsAsync()
    {
        var projects = await _projects.GetAllAsync(includeArchived: true);
        _projectById.Clear();
        foreach (var project in projects)
        {
            _projectById[project.Id] = project;
        }
    }

    /// <summary>一覧を作り直す。作り直す間に一覧側から来る選択の変化（空になる・先頭が選ばれる）は、選び直しとして扱わない。</summary>
    private void RebuildList()
    {
        var today = _clock.LocalToday();
        // GetAllAsync は使用回数の多い順。上位2件（1回以上使ったもの）を「よく使う」に出す（UI 設計書 20.2）
        var frequent = _all.Where(s => s.Template.UseCount > 0).Take(2).Select(s => s.Template.Id).ToHashSet();
        var query = TextNormalizer.ForSearch(SearchText);
        var wasSelecting = _selecting;
        _selecting = true;
        try
        {
            Items.Clear();
            foreach (var summary in _all)
            {
                var template = summary.Template;
                if (query.Length > 0 && !TextNormalizer.ForSearch(template.Name).Contains(query, StringComparison.Ordinal))
                {
                    continue;
                }
                var used = template.LastUsedAt is { } last ? UsedAgo(_clock.ToLocalDate(last), today) : null;
                Items.Add(new TemplateListItem(
                    template.Id,
                    template.Name,
                    template.IconKey,
                    template.ColorHex,
                    summary.ItemCount,
                    template.UseCount,
                    used is null ? $"{summary.ItemCount} 件" : $"{summary.ItemCount} 件 ・ {used}",
                    template.UseCount > 0 ? $"{template.UseCount}回" : null,
                    frequent.Contains(template.Id) ? FrequentGroup : AllGroup));
            }
        }
        finally
        {
            _selecting = wasSelecting;
        }
    }

    /// <summary>一覧を作り直し、出しているテンプレートを（中身は読み直さずに）選び直す。</summary>
    private void RebuildListKeepingSelection()
    {
        var shownId = _current?.Template.Id;
        RebuildList();
        _selecting = true;
        try
        {
            Selected = Items.FirstOrDefault(i => i.Id == shownId);
        }
        finally
        {
            _selecting = false;
        }
    }

    private string LastUsedLabel(TaskTemplate template) =>
        template.LastUsedAt is { } last
            ? $"前回 {_clock.ToLocalDate(last).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)} に使用"
            : "まだ使っていません";

    private async Task LoadDetailAsync(Guid? templateId)
    {
        IsConfirmingDelete = false;
        Notice = null;
        if (templateId is not { } id)
        {
            _current = null;
            Rows.Clear();
            Editor = null;
            Mode = TemplatesMode.Empty;
            return;
        }
        TemplateWithItems? detail;
        try
        {
            detail = await _templates.GetWithItemsAsync(id);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            Fail(ex, "テンプレートを読み込めませんでした", id);
            return;
        }
        if (detail is null)
        {
            Notice = "テンプレートが見つかりません（削除された可能性があります）";
            return;
        }
        ShowDetail(detail);
    }

    private void ShowDetail(TemplateWithItems detail)
    {
        _current = detail;
        var template = detail.Template;
        TemplateName = template.Name;
        TemplateDescription = template.Description;
        LastUsedText = LastUsedLabel(template);
        AnchorCaption = string.IsNullOrWhiteSpace(template.AnchorLabel) ? "基準日" : $"基準日（{template.AnchorLabel}）";

        _suspendPlan = true;
        try
        {
            AnchorDate = _clock.LocalToday();
            ProjectId = template.DefaultProjectId;
            ExpansionTags.Reset(_allTags, template.DefaultTagIds);
            UseTemplateTags = true;
            PullPastToToday = true;
            Rows.Clear();
            var rowByItem = new Dictionary<Guid, ExpansionRow>();
            foreach (var (item, level) in TemplateTree.Flatten(detail.Items))
            {
                var parent = item.ParentItemId is { } p ? rowByItem.GetValueOrDefault(p) : null;
                var row = new ExpansionRow(item.Id, item.Title, level, item.Priority, parent);
                row.PropertyChanged += OnRowChanged;
                rowByItem[item.Id] = row;
                Rows.Add(row);
            }
        }
        finally
        {
            _suspendPlan = false;
        }
        Editor = null;
        Mode = TemplatesMode.Preview;
        RefreshPlan();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExpansionRow.IsIncluded))
        {
            RefreshPlan();
        }
    }

    /// <summary>プレビューを作り直す: 行の日付・親が外れたかどうか・作られる件数・過去日の警告。</summary>
    private void RefreshPlan()
    {
        if (_current is null || _suspendPlan)
        {
            return;
        }
        var today = _clock.LocalToday();
        // 外した行の日付も見せたいので、表示用には全項目で計画する
        var display = _expander.Plan(EffectiveTemplate(), _current.Items, Options(new HashSet<Guid>()));
        var planned = display.Tasks.ToDictionary(t => t.SourceItemId);
        foreach (var row in Rows)
        {
            row.IsParentExcluded = HasExcludedAncestor(row);
            if (planned.TryGetValue(row.ItemId, out var task))
            {
                ApplyDates(row, task, today);
            }
        }

        var plan = BuildPlan();
        CreateCount = plan.Tasks.Count;
        PastCount = plan.PastCount;
        var pastDates = plan.Tasks.Where(t => t.IsPast && t.OriginalDate is not null)
            .Select(t => t.OriginalDate!.Value).Distinct().Order().ToList();
        PastWarningText = pastDates.Count == 0
            ? null
            : $"{plan.PastCount} 件の期限が過去の日付（{string.Join("・", pastDates.Take(3).Select(DateLabels.MonthDay))}{(pastDates.Count > 3 ? " ほか" : "")}）になります。";
        PullPastText = $"過去になるぶんは今日（{DateLabels.MonthDay(today)}）にまとめる";
    }

    private void ApplyDates(ExpansionRow row, PlannedTask task, DateOnly today)
    {
        row.OffsetText = task.DueOffsetDays is { } offset ? TemplateExpander.DescribeOffset(offset) : null;
        row.IsPast = task.IsPast;
        if (task.ResolvedDate is not { } date)
        {
            row.DateText = null;
            row.IsToday = false;
            return;
        }
        var time = task.DueTime is { } t ? " " + DateLabels.Time(t) : "";
        var shownAsPast = task.IsPast && !PullPastToToday;
        row.IsToday = !shownAsPast && date == today;
        var suffix = shownAsPast ? "過去" : row.IsToday ? "今日" : "";
        row.DateText = DateLabels.Short(date) + suffix + time;
    }

    private static bool HasExcludedAncestor(ExpansionRow row)
    {
        for (var parent = row.Parent; parent is not null; parent = parent.Parent)
        {
            if (!parent.IsIncluded)
            {
                return true;
            }
        }
        return false;
    }

    private ExpansionPlan BuildPlan() =>
        _expander.Plan(EffectiveTemplate(), _current!.Items, Options(Rows.Where(r => !r.IsIncluded).Select(r => r.ItemId).ToHashSet()));

    private ExpansionOptions Options(IReadOnlySet<Guid> excluded) => new()
    {
        AnchorDate = AnchorDate,
        ExcludedItemIds = excluded,
        ProjectOverride = ProjectId,
        TagOverride = UseTemplateTags ? null : ExpansionTags.SelectedIds,
        PullPastToToday = PullPastToToday,
    };

    /// <summary>
    /// 「まとめて入れる先」で「なし」を選んだとき、ExpansionOptions.ProjectOverride=null だとテンプレートの既定に戻ってしまう。
    /// そのときだけ既定のプロジェクトを外した写しを渡す。
    /// </summary>
    private TaskTemplate EffectiveTemplate()
    {
        var template = _current!.Template;
        return ProjectId is null && template.DefaultProjectId is not null
            ? new TaskTemplate { Id = template.Id, Name = template.Name, DefaultTagIds = template.DefaultTagIds }
            : template;
    }

    /// <summary>「今日使用」「昨日使用」「3日前」「先週使用」「2週間前」「3か月前」「1年前」。</summary>
    internal static string UsedAgo(DateOnly usedOn, DateOnly today)
    {
        var days = today.DayNumber - usedOn.DayNumber;
        return days switch
        {
            <= 0 => "今日使用",
            1 => "昨日使用",
            < 7 => $"{days}日前",
            < 14 => "先週使用",
            < 30 => $"{days / 7}週間前",
            < 365 => $"{days / 30}か月前",
            _ => $"{days / 365}年前",
        };
    }

    /// <summary>
    /// 読み書きで起こりうる失敗（DB・ファイル・リポジトリの検証）。これ以外（プログラムの誤り）は捕まえず、
    /// アプリ全体のハンドラ（ログ＋画面の知らせ）に任せる。
    /// </summary>
    internal static bool IsDataError(Exception ex) =>
        ex is DbException or IOException or InvalidOperationException or ArgumentException
        || ex.InnerException is DbException;

    private void Fail(Exception ex, string message, Guid? templateId = null)
    {
        Notice = $"{message}（ログに記録しました）";
        _logger.LogError(ex, "テンプレート画面: {Message}（テンプレート {TemplateId}）", message, templateId);
    }
}

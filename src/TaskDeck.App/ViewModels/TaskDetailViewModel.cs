using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDeck.App.Services;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using RecurrenceWords = TaskDeck.Core.Services.RecurrenceText;

namespace TaskDeck.App.ViewModels;

/// <summary>通知・所要時間のような「値を選ぶ」一覧の1項目。</summary>
public sealed record ChoiceOption(string Label, int? Value);

public sealed record StatusOption(string Label, TaskItemStatus Value);

/// <summary>タグのチップ、またはタグ入力の候補。IsCreate の候補は「新しく作る」。</summary>
public sealed partial class TagChipViewModel(Guid id, string name, bool isCreate = false) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public bool IsCreate { get; } = isCreate;

    /// <summary>候補の表示（「#仕事」／「＋「仕事」を作る」）。</summary>
    public string Label => IsCreate ? $"＋「{Name}」を作る" : "#" + Name;

    public string RemoveName => $"タグ「{Name}」を外す";
}

/// <summary>詳細ペインのサブタスク1件（波1では表示と完了切替だけ）。</summary>
public sealed partial class SubtaskViewModel : ObservableObject
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required bool IsCompleted { get; init; }
}

/// <summary>
/// 右の詳細ペイン（S-06）。値の変更はその場で保存し、取り消しは UndoService に積む。
/// ピッカーは View 側の Popup に入れ、結果をここのメソッドで受ける。
/// </summary>
public sealed partial class TaskDetailViewModel : ObservableObject
{
    private const int MaxTagSuggestions = 6;

    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly ITagRepository _tags;
    private readonly IClock _clock;
    private readonly UndoService _undo;
    private readonly ThemeService _theme;

    private IReadOnlyList<Tag> _allTags = [];
    private string? _projectSourceColorHex;
    private int _loadToken;

    public TaskDetailViewModel(
        ITaskRepository tasks,
        IProjectRepository projects,
        ITagRepository tags,
        IClock clock,
        UndoService undo,
        ThemeService theme)
    {
        _tasks = tasks;
        _projects = projects;
        _tags = tags;
        _clock = clock;
        _undo = undo;
        _theme = theme;
        _theme.ThemeChanged += (_, _) => ProjectColorHex = ThemeColor(_projectSourceColorHex);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTask))]
    private Guid? _taskId;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private TaskItemStatus _status;

    [ObservableProperty]
    private string _statusText = "未着手";

    [ObservableProperty]
    private string _dueText = "なし";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetReminder))]
    private bool _hasDue;

    [ObservableProperty]
    private string _priorityText = "なし";

    [ObservableProperty]
    private string _prioritySymbol = "";

    [ObservableProperty]
    private Priority _priority;

    [ObservableProperty]
    private string _projectText = "なし";

    [ObservableProperty]
    private string? _projectColorHex;

    [ObservableProperty]
    private string _recurrenceText = "なし";

    [ObservableProperty]
    private string _reminderText = "なし";

    [ObservableProperty]
    private string _durationText = "なし";

    [ObservableProperty]
    private string _createdUpdatedText = "";

    [ObservableProperty]
    private string _subtaskProgressText = "0 / 0";

    [ObservableProperty]
    private double _subtaskProgressValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubtaskSection))]
    private bool _hasSubtasks;

    /// <summary>サブタスクを足せる（3階層目・ゴミ箱の中は足せない F-022）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubtaskSection))]
    private bool _canAddSubtask;

    /// <summary>「サブタスクを追加」の入力欄（Enter で追加して、欄に残る）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubtaskInput))]
    private string _subtaskInput = "";

    /// <summary>入力欄に文字がある（「追加」ボタンを出す）。</summary>
    public bool HasSubtaskInput => !string.IsNullOrWhiteSpace(SubtaskInput);

    [ObservableProperty]
    private string _tagInput = "";

    [ObservableProperty]
    private bool _isTagSuggestionOpen;

    public ObservableCollection<TagChipViewModel> Tags { get; } = [];

    public ObservableCollection<SubtaskViewModel> Subtasks { get; } = [];

    public ObservableCollection<TagChipViewModel> TagSuggestions { get; } = [];

    public bool HasTask => TaskId is not null;

    /// <summary>サブタスクの欄を出すか（子がいるか、足せるとき）。</summary>
    public bool ShowSubtaskSection => HasSubtasks || CanAddSubtask;

    /// <summary>はい／いいえ／キャンセルの確認（View が差し込む。既定は「はい」）。親を完了にするとき使う（F-024）。</summary>
    public Func<ConfirmRequest, ConfirmChoice> Confirm { get; set; } = _ => ConfirmChoice.Yes;

    /// <summary>通知は期限からの相対なので、期限が無いときは選べない。</summary>
    public bool CanSetReminder => HasDue;

    /// <summary>期限（ローカル日付。ピッカーに入れる現在値）。</summary>
    public DateOnly? DueDate { get; private set; }

    public TimeOnly? DueTime { get; private set; }

    /// <summary>繰り返しピッカーのプレビュー起点。</summary>
    public DateTime? BaseDueAt { get; private set; }

    public bool DueHasTime { get; private set; }

    public Guid? ProjectId { get; private set; }

    public RecurrenceInput? Recurrence { get; private set; }

    public int? RemindOffsetMinutes { get; private set; }

    public int? DurationMinutes { get; private set; }

    public IReadOnlyList<StatusOption> StatusOptions { get; } =
    [
        new("未着手", TaskItemStatus.NotStarted),
        new("進行中", TaskItemStatus.InProgress),
        new("完了", TaskItemStatus.Completed),
        new("中止", TaskItemStatus.Cancelled),
    ];

    public IReadOnlyList<ChoiceOption> ReminderOptions { get; } =
    [
        new("なし", null),
        new("期限の時刻", 0),
        new("5分前", 5),
        new("15分前", 15),
        new("30分前", 30),
        new("1時間前", 60),
        new("1日前", 1440),
    ];

    /// <summary>
    /// 所要時間の選択肢（文言は値の表示と同じ形「1時間30分」）。カレンダーで伸ばした 75分 のように選択肢に無い値でも、
    /// 表示は DurationName で「1時間15分」と出し、ここからどれかを選べばその値に入れ直せる。
    /// </summary>
    public IReadOnlyList<ChoiceOption> DurationOptions { get; } =
    [
        new("なし", null),
        .. new[] { 15, 30, 45, 60, 90, 120 }.Select(m => new ChoiceOption(DisplayText.DurationName(m), m)),
    ];

    /// <summary>ペインの「削除」が押された（一覧の側で、次の行を選び直しながら消す）。</summary>
    public event EventHandler<(Guid Id, string Title)>? DeleteRequested;

    /// <summary>選択中のタスクを読み込む（null で空にする）。速く選び直したときは最後の1回だけを反映する。</summary>
    public async Task LoadAsync(Guid? id)
    {
        var token = ++_loadToken;
        if (id is not { } taskId)
        {
            Clear();
            return;
        }
        var detail = await _tasks.GetDetailAsync(taskId);
        if (token != _loadToken)
        {
            return;
        }
        if (detail is null)
        {
            Clear();
            return;
        }
        var project = detail.Task.ProjectId is { } pid ? await _projects.GetAsync(pid) : null;
        var allTags = await _tags.GetAllAsync();
        if (token != _loadToken)
        {
            return;
        }
        Apply(detail, project, allTags);
    }

    /// <summary>データが変わったときの読み直し。</summary>
    public Task RefreshAsync() => LoadAsync(TaskId);

    public async Task CommitTitleAsync(string text)
    {
        var title = TextNormalizer.ForTitle(text, TaskItem.TitleMaxLength);
        if (TaskId is not { } id || title == Title)
        {
            return;
        }
        if (title.Length == 0)
        {
            // 空にしたら直前の値に戻す（open_issues 4.2）
            OnPropertyChanged(nameof(Title));
            return;
        }
        var before = Title;
        var result = await _tasks.UpdateAsync(id, t => t.Title = title);
        _undo.Record(result, DisplayText.Quote(before) + "の名前を変えました", UndoKind.TextEdit, showToast: false);
        if (TaskId == id)
        {
            Title = result.Affected.FirstOrDefault(t => t.Id == id)?.Title ?? title;
        }
    }

    public async Task CommitNotesAsync(string text)
    {
        if (TaskId is not { } id || text == Notes)
        {
            return;
        }
        var result = await _tasks.UpdateAsync(id, t => t.Notes = string.IsNullOrWhiteSpace(text) ? null : text);
        _undo.Record(result, "メモを変えました", UndoKind.TextEdit, showToast: false);
        if (TaskId == id)
        {
            Notes = text;
        }
    }

    [RelayCommand]
    private async Task SetStatusAsync(StatusOption? option)
    {
        if (option is null || TaskId is not { } id || option.Value == Status)
        {
            return;
        }
        TaskMutationResult result;
        if (option.Value == TaskItemStatus.Completed && Subtasks.Any(s => !s.IsCompleted))
        {
            // 未完了のサブタスクがある親の完了は、サブタスクもまとめるかを聞く（F-024）
            if (await SubtaskCompletion.ResolveAsync(_tasks, [id], [id], DisplayText.Quote(Title), Confirm) is not { } ids)
            {
                return;
            }
            result = await _tasks.SetCompletedAsync(ids, true);
        }
        else
        {
            result = await _tasks.UpdateAsync(id, t => t.Status = option.Value);
        }
        // 繰り返しのタスクを完了にすると次回ができる。一覧のチェックと同じく次回の日付をトーストで見せる（UI 設計書 13.1）
        if (result.Created.Select(t => t.DueAt).FirstOrDefault(d => d is not null) is { } nextDue)
        {
            _undo.Record(result, "完了しました・次回は " + DisplayText.DateWithWeekday(_clock.ToLocalDate(nextDue)), UndoKind.Toggle, showToast: true);
        }
        else
        {
            _undo.Record(result, $"状態を「{option.Label}」にしました", UndoKind.Other, showToast: false);
        }
        await RefreshAsync();
    }

    /// <summary>日付ピッカーの結果（Date=null で期限を消す、Time=null で終日）。</summary>
    public async Task ApplyDueAsync(DateOnly? date, TimeOnly? time)
    {
        if (TaskId is not { } id)
        {
            return;
        }
        var result = await _tasks.UpdateAsync(id, t =>
        {
            if (date is not { } day)
            {
                t.DueAt = null;
                t.DueHasTime = false;
                return;
            }
            t.DueHasTime = time is not null;
            t.DueAt = time is { } at ? _clock.LocalToUtc(day, at) : TaskRules.DateOnlyDue(day, _clock);
        });
        _undo.Record(result, date is null ? "期限を消しました" : "期限を変えました", UndoKind.Other, showToast: false);
        await RefreshAsync();
    }

    public async Task SetPriorityAsync(Priority priority)
    {
        if (TaskId is not { } id || priority == Priority)
        {
            return;
        }
        var result = await _tasks.UpdateAsync(id, t => t.Priority = priority);
        _undo.Record(result, $"優先度を「{DisplayText.PriorityName(priority)}」にしました", UndoKind.Other, showToast: false);
        await RefreshAsync();
    }

    public async Task SetProjectAsync(Guid? projectId)
    {
        if (TaskId is not { } id || projectId == ProjectId)
        {
            return;
        }
        var result = await _tasks.UpdateAsync(id, t => t.ProjectId = projectId);
        _undo.Record(result, "プロジェクトを変えました", UndoKind.Other, showToast: false);
        await RefreshAsync();
    }

    public async Task SetRecurrenceAsync(RecurrenceInput? recurrence)
    {
        if (TaskId is not { } id)
        {
            return;
        }
        var result = await _tasks.SetRecurrenceAsync(id, recurrence);
        _undo.Record(result, recurrence is null ? "繰り返しを解除しました" : "繰り返しを設定しました", UndoKind.Other, showToast: false);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SetReminderAsync(ChoiceOption? option)
    {
        if (option is null || TaskId is not { } id || option.Value == RemindOffsetMinutes)
        {
            return;
        }
        var result = await _tasks.UpdateAsync(id, t => t.RemindOffsetMinutes = option.Value);
        _undo.Record(result, $"通知を「{option.Label}」にしました", UndoKind.Other, showToast: false);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SetDurationAsync(ChoiceOption? option)
    {
        if (option is null || TaskId is not { } id || option.Value == DurationMinutes)
        {
            return;
        }
        var result = await _tasks.UpdateAsync(id, t => t.DurationMinutes = option.Value);
        _undo.Record(result, $"所要時間を「{option.Label}」にしました", UndoKind.Other, showToast: false);
        await RefreshAsync();
    }

    /// <summary>タグを足す（無ければ作る F-044）。</summary>
    public async Task AddTagAsync(string name)
    {
        var normalized = TextNormalizer.ForName(name.Trim().TrimStart('#', '＃'));
        if (TaskId is not { } id || normalized.Length == 0)
        {
            return;
        }
        var tag = await _tags.GetOrCreateAsync(normalized);
        TagInput = "";
        IsTagSuggestionOpen = false;
        if (Tags.Any(t => t.Id == tag.Id))
        {
            return;
        }
        var ids = Tags.Select(t => t.Id).Append(tag.Id).ToList();
        var result = await _tasks.SetTagsAsync(id, ids);
        _undo.Record(result, $"タグ「{tag.Name}」を付けました", UndoKind.Other, showToast: false);
        await RefreshAsync();
    }

    /// <summary>タグ入力の候補を選んだ（既存のタグ、または「新しく作る」）。</summary>
    public Task PickTagSuggestionAsync(TagChipViewModel suggestion) => AddTagAsync(suggestion.Name);

    [RelayCommand]
    private async Task RemoveTagAsync(TagChipViewModel? chip)
    {
        if (chip is null || TaskId is not { } id)
        {
            return;
        }
        var ids = Tags.Where(t => t.Id != chip.Id).Select(t => t.Id).ToList();
        var result = await _tasks.SetTagsAsync(id, ids);
        _undo.Record(result, $"タグ「{chip.Name}」を外しました", UndoKind.Other, showToast: false);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ToggleSubtaskAsync(SubtaskViewModel? subtask)
    {
        if (subtask is null)
        {
            return;
        }
        IReadOnlyList<Guid> ids = [subtask.Id];
        if (!subtask.IsCompleted)
        {
            // サブタスクの下にさらに未完了の子（孫）がいれば、まとめるかを聞く（F-024）
            if (await SubtaskCompletion.ResolveAsync(_tasks, ids, ids, DisplayText.Quote(subtask.Title), Confirm) is not { } resolved)
            {
                await RefreshAsync();   // 押したチェックを元に戻す
                return;
            }
            ids = resolved;
        }
        var result = await _tasks.SetCompletedAsync(ids, !subtask.IsCompleted);
        var label = DisplayText.Quote(subtask.Title) + (subtask.IsCompleted ? "を未完了に戻しました" : "を完了しました");
        _undo.Record(result, label, UndoKind.Toggle, showToast: false);
        await RefreshAsync();
    }

    /// <summary>
    /// 「サブタスクを追加」（F-021）。入力欄の文字をこのタスクの子として足す（プロジェクトは親と同じにする）。
    /// 入力欄は空にして残す（続けて打てるように）。3階層目・ゴミ箱の中では足さない。
    /// </summary>
    public async Task AddSubtaskAsync()
    {
        var text = SubtaskInput;
        var title = TextNormalizer.ForTitle(text, TaskItem.TitleMaxLength);
        if (TaskId is not { } id || !CanAddSubtask || title.Length == 0)
        {
            return;
        }
        var result = await _tasks.AddAsync(new NewTaskRequest { Title = title, ParentTaskId = id, ProjectId = ProjectId });
        _undo.Record(result, "サブタスク" + DisplayText.Quote(title) + "を追加しました", UndoKind.Other, showToast: false);
        if (TaskId == id && SubtaskInput == text)
        {
            SubtaskInput = "";
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void Delete()
    {
        if (TaskId is { } id)
        {
            DeleteRequested?.Invoke(this, (id, Title));
        }
    }

    /// <summary>
    /// タグ入力の候補: まだ付いていない既存のタグのうち入力を含むもの。
    /// 入力と同じ名前のタグが無ければ、最後に「新しく作る」を足す（マウスでも新しいタグを作れるように）。
    /// </summary>
    public void UpdateTagSuggestions()
    {
        TagSuggestions.Clear();
        var typed = TextNormalizer.ForName(TagInput.Trim().TrimStart('#', '＃'));
        var key = TextNormalizer.ForSearch(typed);
        foreach (var tag in _allTags)
        {
            if (Tags.Any(t => t.Id == tag.Id))
            {
                continue;
            }
            if (key.Length > 0 && !TextNormalizer.ForSearch(tag.Name).Contains(key, StringComparison.Ordinal))
            {
                continue;
            }
            TagSuggestions.Add(new TagChipViewModel(tag.Id, tag.Name));
            if (TagSuggestions.Count >= MaxTagSuggestions)
            {
                break;
            }
        }
        var exists = _allTags.Any(t => string.Equals(TextNormalizer.ForSearch(t.Name), key, StringComparison.Ordinal));
        if (typed.Length > 0 && !exists)
        {
            TagSuggestions.Add(new TagChipViewModel(Guid.Empty, typed, isCreate: true));
        }
        IsTagSuggestionOpen = TagSuggestions.Count > 0;
    }

    private void Clear()
    {
        TaskId = null;
        Title = "";
        Notes = "";
        Tags.Clear();
        Subtasks.Clear();
        HasSubtasks = false;
        CanAddSubtask = false;
        SubtaskInput = "";
        TagInput = "";
        IsTagSuggestionOpen = false;
    }

    private void Apply(TaskDetail detail, Project? project, IReadOnlyList<Tag> allTags)
    {
        var task = detail.Task;
        if (TaskId != task.Id)
        {
            // 書きかけのサブタスク名を別のタスクに持ち越さない
            SubtaskInput = "";
        }
        TaskId = task.Id;
        CanAddSubtask = !task.IsDeleted && task.Depth < TaskItem.MaxDepth;
        Title = task.Title;
        Notes = task.Notes ?? "";
        Status = task.Status;
        StatusText = DisplayText.StatusName(task.Status);
        Priority = task.Priority;
        PriorityText = DisplayText.PriorityName(task.Priority);
        PrioritySymbol = DisplayText.PrioritySymbol(task.Priority);
        DueHasTime = task.DueHasTime;
        BaseDueAt = task.DueAt;
        HasDue = task.DueAt is not null;
        DueDate = task.DueAt is { } due ? _clock.ToLocalDate(due) : null;
        DueTime = task.DueAt is { } dueTime && task.DueHasTime ? TimeOnly.FromDateTime(_clock.ToLocal(dueTime)) : null;
        DueText = DisplayText.DueLong(task.DueAt, task.DueHasTime, _clock);
        RemindOffsetMinutes = task.RemindOffsetMinutes;
        ReminderText = DisplayText.ReminderName(task.RemindOffsetMinutes);
        DurationMinutes = task.DurationMinutes;
        DurationText = DisplayText.DurationName(task.DurationMinutes);
        Recurrence = detail.Rule is { } rule
            ? new RecurrenceInput(rule.RRule, rule.BaseKind, rule.EndKind, rule.EndDate, rule.MaxOccurrences)
            : null;
        RecurrenceText = detail.Rule is { } r ? RecurrenceWords.Describe(r.RRule) : "なし";
        CreatedUpdatedText = DisplayText.CreatedUpdated(task.CreatedAt, task.UpdatedAt, _clock);

        ProjectId = task.ProjectId;
        ProjectText = project?.Name ?? "なし";
        _projectSourceColorHex = project?.ColorHex;
        ProjectColorHex = ThemeColor(_projectSourceColorHex);

        _allTags = allTags;
        var names = allTags.ToDictionary(t => t.Id, t => t.Name);
        Tags.Clear();
        foreach (var tagId in detail.TagIds.Where(names.ContainsKey))
        {
            Tags.Add(new TagChipViewModel(tagId, names[tagId]));
        }

        Subtasks.Clear();
        foreach (var child in detail.Children)
        {
            Subtasks.Add(new SubtaskViewModel
            {
                Id = child.Id,
                Title = child.Title,
                IsCompleted = !child.IsOpen,
            });
        }
        HasSubtasks = Subtasks.Count > 0;
        var done = Subtasks.Count(s => s.IsCompleted);
        SubtaskProgressText = string.Create(CultureInfo.InvariantCulture, $"{done} / {Subtasks.Count}");
        SubtaskProgressValue = Subtasks.Count == 0 ? 0 : (double)done / Subtasks.Count;
    }

    private string? ThemeColor(string? sourceHex) => sourceHex is null ? null : ProjectPalette.ForTheme(sourceHex, _theme.IsDark);
}

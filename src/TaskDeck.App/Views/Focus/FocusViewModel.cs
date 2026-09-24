using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Focus;

/// <summary>
/// フォーカスモード（S-14、UI 設計書 25、F-171〜F-177）。担当: 波3-J。View の型は持たない。
///
/// 決めごと:
/// - 出すのは今日の未完了（期限切れを含む。「今日」ビューと同じ条件）。サブタスクも自分の期限が今日までなら1件として出す
/// - 並びは <see cref="FocusText.Order{T}"/>（期限の近い順 → 優先度の高い順）。「今はやらない」は列の後ろに回すだけで期限は変えない
/// - 完了率（上端のバー）= 今日完了した数 ÷（今日完了した数 ＋ 残り）。今日完了した数は、期限が今日までで今日閉じた「完了」（中止は数えない）
/// - 「5 件目 / 11 件」は 完了した数＋1 ／ 全体、「残り」は今出している1件を除いた数
/// - 次の1件を出す前に読み直し、ほかの画面で片づいたものは飛ばす
/// - 完了は取り消しを記録する（トーストは繰り返しの次回のときだけ）。Ctrl+Z はこの画面で完了にした直前の1件だけを戻して列に戻す
/// - 開いた状態は保持しない（閉じたら次はまた先頭から F-177）
/// </summary>
public sealed partial class FocusViewModel : ObservableObject
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projectRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IClock _clock;
    private readonly UndoService _undo;
    private readonly ILogger<FocusViewModel> _logger;
    private readonly List<TaskListRow> _queue = [];

    private Dictionary<Guid, Project> _projects = [];
    private Dictionary<Guid, string> _tagNames = [];
    private (IReadOnlyList<TaskListRow> Rows, UndoEntry? Entry)? _lastCompletion;
    private bool _busy;

    public FocusViewModel(
        ITaskRepository tasks,
        IProjectRepository projects,
        ITagRepository tags,
        IClock clock,
        UndoService undo,
        ILogger<FocusViewModel> logger)
    {
        _tasks = tasks;
        _projectRepository = projects;
        _tagRepository = tags;
        _clock = clock;
        _undo = undo;
        _logger = logger;
    }

    /// <summary>いま出している1件（全部終わった・0件なら null）。</summary>
    [ObservableProperty]
    private FocusCard? _current;

    /// <summary>今日完了した数（開いたときの数に、この画面で完了した数を足したもの）。</summary>
    [ObservableProperty]
    private int _doneCount;

    [ObservableProperty]
    private bool _isLoaded;

    /// <summary>直前の操作の知らせ（「〇〇を完了しました」）。</summary>
    [ObservableProperty]
    private string _notice = "";

    public int TotalCount => DoneCount + _queue.Count;

    /// <summary>今日の完了率（0〜1）。</summary>
    public double Progress => TotalCount == 0 ? 0 : (double)DoneCount / TotalCount;

    public string ProgressText => string.Create(CultureInfo.InvariantCulture, $"今日の完了率 {Math.Round(Progress * 100)}%");

    public string RemainingText => string.Create(CultureInfo.InvariantCulture, $"今日 ・ 残り {Math.Max(0, _queue.Count - 1)} 件");

    public string PositionText => Current is null
        ? string.Create(CultureInfo.InvariantCulture, $"{DoneCount} 件 / {TotalCount} 件")
        : string.Create(CultureInfo.InvariantCulture, $"{DoneCount + 1} 件目 / {TotalCount} 件");

    public bool HasCurrent => Current is not null;

    /// <summary>開いたときから今日のタスクが1件も無い。</summary>
    public bool IsEmpty => IsLoaded && TotalCount == 0;

    /// <summary>今日の分を全部片づけた。</summary>
    public bool IsFinished => IsLoaded && TotalCount > 0 && Current is null;

    public string FinishedText => string.Create(CultureInfo.InvariantCulture, $"今日は {DoneCount} 件を完了しました");

    /// <summary>この画面で完了にした直前の1件を Ctrl+Z で戻せるか（その後に別の操作を積んでいたら戻さない）。</summary>
    public bool CanUndo => _lastCompletion is { } last && ReferenceEquals(_undo.Stack.Peek(), last.Entry);

    /// <summary>未完了のサブタスクがある親を完了にするときの確認（F-024）。View が MessageBox を出す。</summary>
    public Func<ConfirmRequest, ConfirmChoice> Confirm { get; set; } = _ => ConfirmChoice.No;

    /// <summary>開いたときに1回。今日のタスクを並べ、今日完了した数を数えて、先頭の1件を出す。</summary>
    public async Task InitializeAsync()
    {
        _projects = (await _projectRepository.GetAllAsync(includeArchived: true)).ToDictionary(p => p.Id);
        _tagNames = (await _tagRepository.GetAllAsync()).ToDictionary(t => t.Id, t => t.Name);
        var open = await _tasks.QueryAsync(BuiltInViews.QueryFor(ViewKey.Today) with { IncludeSubtasks = false });
        var done = await _tasks.QueryAsync(new TaskQuery
        {
            Statuses = [TaskItemStatus.Completed],
            Due = DueFilter.TodayOrOverdue,
            ClosedFrom = _clock.LocalToday(),
            IncludeSubtasks = false,
        });
        _queue.AddRange(FocusText.Order(open.Where(r => !r.IsContext), r => r.Task, _clock));
        DoneCount = done.Count;
        await ShowNextAsync();
        IsLoaded = true;
        RaiseState();
        _logger.LogInformation("フォーカスモードを始めました（残り {Open} 件・今日完了 {Done} 件）", _queue.Count, DoneCount);
    }

    /// <summary>Space・「完了して次へ」（F-173）。</summary>
    public async Task CompleteAsync()
    {
        if (_busy || Current is not { } card)
        {
            return;
        }
        _busy = true;
        try
        {
            IReadOnlyList<Guid> ids = [card.TaskId];
            if (card.HasOpenSubtasks)
            {
                if (await SubtaskCompletion.ResolveAsync(_tasks, ids, ids, DisplayText.Quote(card.Title), Confirm) is not { } resolved)
                {
                    return;
                }
                ids = resolved;
            }
            var result = await _tasks.SetCompletedAsync(ids, true);
            var next = result.Created.Select(t => t.DueAt).FirstOrDefault(d => d is not null);
            var label = result.Created.Count == 0
                ? DisplayText.Quote(card.Title) + "を完了しました"
                : next is { } due
                    ? "完了しました・次回は " + DisplayText.DateWithWeekday(_clock.ToLocalDate(due))
                    : "完了しました・次回を作りました";
            _undo.Record(result, label, UndoKind.Toggle, showToast: result.Created.Count > 0);

            // 親と一緒にまとめて完了したサブタスクが列にあれば、それも片づいたものとして抜く
            var removed = _queue.Where(r => ids.Contains(r.Task.Id)).ToList();
            _queue.RemoveAll(r => ids.Contains(r.Task.Id));
            DoneCount += removed.Count;
            _lastCompletion = result.Changes.IsEmpty ? null : (removed, _undo.Stack.Peek());
            Notice = label;
            _logger.LogInformation("フォーカスモードで完了にしました {Id}", card.TaskId);
            await ShowNextAsync();
        }
        finally
        {
            _busy = false;
            RaiseState();
        }
    }

    /// <summary>→・「今はやらない」（F-174）: 列の後ろに回す。期限は変えない。</summary>
    public async Task SkipAsync()
    {
        if (_busy || Current is not { } card)
        {
            return;
        }
        if (_queue.Count < 2)
        {
            Notice = "ほかに今日のタスクはありません";
            return;
        }
        _busy = true;
        try
        {
            var row = _queue[0];
            _queue.RemoveAt(0);
            _queue.Add(row);
            Notice = DisplayText.Quote(card.Title) + "を後に回しました";
            await ShowNextAsync();
        }
        finally
        {
            _busy = false;
            RaiseState();
        }
    }

    /// <summary>Ctrl+Z: この画面で完了にした直前の1件を戻して、列の先頭に戻す。</summary>
    public async Task UndoAsync()
    {
        if (_busy || !CanUndo || _lastCompletion is not { } last)
        {
            return;
        }
        _busy = true;
        try
        {
            if (!await _undo.UndoLatestAsync())
            {
                return;
            }
            _queue.InsertRange(0, last.Rows);
            DoneCount -= last.Rows.Count;
            _lastCompletion = null;
            Notice = "元に戻しました";
            await ShowNextAsync();
        }
        finally
        {
            _busy = false;
            RaiseState();
        }
    }

    /// <summary>サブタスクのチェック（取り消しを記録。トーストは出さない）。</summary>
    public async Task ToggleSubtaskAsync(FocusSubtask subtask)
    {
        if (_busy || Current is not { } card)
        {
            subtask.RefreshCheck();
            return;
        }
        _busy = true;
        try
        {
            var done = !subtask.IsDone;
            var result = await _tasks.SetCompletedAsync([subtask.Id], done);
            if (result.Changes.IsEmpty)
            {
                subtask.RefreshCheck();
                return;
            }
            _undo.Record(result, "サブタスク" + DisplayText.Quote(subtask.Title) + (done ? "を完了しました" : "を未完了に戻しました"), UndoKind.Toggle, showToast: false);
            subtask.IsDone = done;
            card.RecountSubtasks();
        }
        finally
        {
            _busy = false;
            RaiseState();
        }
    }

    /// <summary>残り時間を今の時刻で出し直す（画面が30秒ごとに呼ぶ）。</summary>
    public void RefreshClock() => Current?.UpdateTimes(_clock);

    /// <summary>
    /// 列の先頭を読み直して出す。ほかの画面で片づいた・消されたものは飛ばす（今日完了したものは完了の数に入れる）。
    /// </summary>
    private async Task ShowNextAsync()
    {
        while (_queue.Count > 0)
        {
            var detail = await _tasks.GetDetailAsync(_queue[0].Task.Id);
            if (detail is { Task: { IsDeleted: false, IsOpen: true } task })
            {
                Current = CreateCard(task, detail);
                return;
            }
            _queue.RemoveAt(0);
            if (detail?.Task is { IsDeleted: false, Status: TaskItemStatus.Completed, CompletedAt: { } closed } && _clock.ToLocalDate(closed) == _clock.LocalToday())
            {
                DoneCount++;
            }
        }
        Current = null;
    }

    private FocusCard CreateCard(TaskItem task, TaskDetail detail)
    {
        var project = task.ProjectId is { } id && _projects.TryGetValue(id, out var p) ? p : null;
        var card = new FocusCard
        {
            Task = task,
            ProjectName = project?.Name,
            ProjectColorHex = project is null ? null : ProjectPalette.ForTheme(project.ColorHex, dark: true),
            TagsText = string.Join("  ", detail.TagIds.Where(_tagNames.ContainsKey).Select(t => "#" + _tagNames[t])),
            NotesHead = FocusText.NotesHead(task.Notes),
            Subtasks = [.. detail.Children.Select(c => new FocusSubtask(c.Id, c.Title, !c.IsOpen))],
        };
        card.UpdateTimes(_clock);
        return card;
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(FinishedText));
        OnPropertyChanged(nameof(CanUndo));
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.ViewModels;

/// <summary>「プロジェクトへ移動」の行き先（Id=null は「なし」）。ColorHex はテーマに合わせた色。</summary>
public sealed record MoveTarget(Guid? Id, string Name, string? ColorHex);

/// <summary>
/// 選んだタスクへの操作（波2-E）。一括ツールバー（S-10）・行の右クリックメニュー・ドロップの行き先から使う。
/// 1件なら単発の操作として積み（取り消しの種類は Other）、2件以上なら一括操作として取り消し1段（UndoKind.Bulk）にまとめて
/// トースト「3 件を完了にしました」を出す（UI 設計書 13.1・13.3）。ゴミ箱の行は対象にしない。
/// </summary>
public sealed partial class TaskListViewModel
{
    /// <summary>「タグを付ける」の候補（開くたびに LoadTagChoicesAsync で読み直す）。</summary>
    public ObservableCollection<TagChipViewModel> TagChoices { get; } = [];

    /// <summary>右クリックメニューの「プロジェクトへ移動」の行き先（開くたびに LoadProjectChoicesAsync で読み直す）。</summary>
    public ObservableCollection<MoveTarget> ProjectChoices { get; } = [];

    /// <summary>今日（ローカル）。右クリックメニューの「今日」「明日」の日付に使う。</summary>
    public DateOnly Today => _clock.LocalToday();

    /// <summary>行の期限（ローカルの日付と時刻。日付ピッカーに入れる現在値）。</summary>
    public (DateOnly? Date, TimeOnly? Time) DueOf(TaskRowViewModel row) => row.Task.DueAt is { } due
        ? (_clock.ToLocalDate(due), row.Task.DueHasTime ? TimeOnly.FromDateTime(_clock.ToLocal(due)) : null)
        : (null, null);

    /// <summary>「プロジェクトへ移動」の行き先を読み直す（「なし」＋アーカイブしていないプロジェクト）。</summary>
    public async Task LoadProjectChoicesAsync()
    {
        var projects = await _projectRepository.GetAllAsync();
        ProjectChoices.Clear();
        ProjectChoices.Add(new MoveTarget(null, "なし", null));
        foreach (var project in projects)
        {
            ProjectChoices.Add(new MoveTarget(project.Id, project.Name, ProjectPalette.ForTheme(project.ColorHex, _theme.IsDark)));
        }
    }

    /// <summary>選んだ未完了のタスクを完了にする（Space・一括ツールバー）。未完了の子がいる親があれば確認する（F-024）。</summary>
    [RelayCommand]
    private async Task CompleteSelectedAsync()
    {
        var rows = _selected.Where(r => r is { IsInTrash: false, IsClosed: false }).ToList();
        if (rows.Count <= 1)
        {
            if (rows.Count == 1)
            {
                await SetCompletedAsync(rows[0], completed: true);
            }
            return;
        }
        IReadOnlyList<Guid> ids = [.. rows.Select(r => r.Id)];
        var parents = rows.Where(r => r.HasOpenSubtasks).Select(r => r.Id).ToList();
        if (parents.Count > 0)
        {
            if (await SubtaskCompletion.ResolveAsync(_tasks, ids, parents, "選んだタスク", Confirm) is not { } resolved)
            {
                return;
            }
            ids = resolved;
        }
        var result = await _tasks.SetCompletedAsync(ids, true);
        _undo.Record(result, Count(rows.Count) + "を完了にしました", UndoKind.Bulk, showToast: true);
        if (LeavesView(completed: true))
        {
            // 消える行は 167ms 残してから消す（UI 設計書 7章）。親の下に添えたサブタスクは親と一緒に動く
            foreach (var row in rows.Where(r => r.Level == 0))
            {
                row.IsFading = true;
            }
            if (SelectedRow is { } primary)
            {
                RememberRemoval(primary);
            }
            ScheduleReload(CompleteFadeMs);
        }
    }

    /// <summary>期限を変える（Date=null で消す、Time=null で終日）。</summary>
    public async Task SetDueForSelectedAsync(DateOnly? date, TimeOnly? time)
    {
        var ids = EditableSelectedIds();
        if (ids.Count == 0)
        {
            return;
        }
        var result = await _tasks.UpdateManyAsync(ids, t => ApplyDue(t, date, time, _clock));
        var what = date is null ? "期限を消しました" : "期限を変えました";
        Record(ids.Count, result, what, Count(ids.Count) + "の" + what, toastSingle: false);
    }

    public async Task SetPriorityForSelectedAsync(Priority priority)
    {
        var ids = EditableSelectedIds();
        if (ids.Count == 0)
        {
            return;
        }
        var result = await _tasks.UpdateManyAsync(ids, t => t.Priority = priority);
        var what = $"優先度を「{DisplayText.PriorityName(priority)}」にしました";
        Record(ids.Count, result, what, Count(ids.Count) + "の" + what, toastSingle: false);
    }

    public Task MoveSelectedToProjectAsync(Guid? projectId) => MoveToProjectAsync(EditableSelectedIds(), projectId);

    /// <summary>
    /// プロジェクトへ移す（一括・右クリック・サイドバーへのドロップ）。いまの一覧から消えることがあるので、1件でもトーストを出す。
    /// </summary>
    public async Task MoveToProjectAsync(IReadOnlyList<Guid> ids, Guid? projectId)
    {
        if (ids.Count == 0)
        {
            return;
        }
        var subject = Subject(ids);
        var result = await _tasks.UpdateManyAsync(ids, t => t.ProjectId = projectId);
        string where;
        if (projectId is not { } pid)
        {
            where = "プロジェクトなしにしました";
        }
        else
        {
            // ピッカーの「新しく作る」で作ったばかりのプロジェクトは、まだ名前の表に無い
            var name = _projects.TryGetValue(pid, out var look) ? look.Name : (await _projectRepository.GetAsync(pid))?.Name;
            where = name is null ? "プロジェクトを変えました" : $"「{name}」へ移しました";
        }
        Record(ids.Count, result, subject + "を" + where, Count(ids.Count) + "を" + where, toastSingle: true);
    }

    /// <summary>「タグを付ける」の候補を読み直す（削除済みを除く、名前順）。</summary>
    public async Task LoadTagChoicesAsync()
    {
        var tags = await _tagRepository.GetAllAsync();
        TagChoices.Clear();
        foreach (var tag in tags)
        {
            TagChoices.Add(new TagChipViewModel(tag.Id, tag.Name));
        }
    }

    public Task AddTagToSelectedAsync(Guid tagId, string tagName) => AddTagAsync(EditableSelectedIds(), tagId, tagName);

    /// <summary>名前でタグを付ける（無ければ作る F-044）。</summary>
    public async Task AddTagByNameToSelectedAsync(string name)
    {
        var normalized = TextNormalizer.ForName(name.Trim().TrimStart('#', '＃'));
        var ids = EditableSelectedIds();
        if (normalized.Length == 0 || ids.Count == 0)
        {
            return;
        }
        var tag = await _tagRepository.GetOrCreateAsync(normalized);
        await AddTagAsync(ids, tag.Id, tag.Name);
    }

    /// <summary>タグを足す（一括・サイドバーのタグへのドロップ）。既に付いているものはそのまま。</summary>
    public async Task AddTagAsync(IReadOnlyList<Guid> ids, Guid tagId, string tagName)
    {
        if (ids.Count == 0)
        {
            return;
        }
        var subject = Subject(ids);
        var result = await _tasks.AddTagsAsync(ids, [tagId]);
        var what = $"タグ「{tagName}」を付けました";
        Record(ids.Count, result, subject + "に" + what, Count(ids.Count) + "に" + what, toastSingle: true);
    }

    /// <summary>日付ピッカーの結果を期限に当てる（日付のみはローカル 0:00 の UTC、時刻ありはその時刻）。</summary>
    internal static void ApplyDue(TaskItem task, DateOnly? date, TimeOnly? time, IClock clock)
    {
        if (date is not { } day)
        {
            task.DueAt = null;
            task.DueHasTime = false;
            return;
        }
        task.DueHasTime = time is not null;
        task.DueAt = time is { } at ? clock.LocalToUtc(day, at) : TaskRules.DateOnlyDue(day, clock);
    }

    /// <summary>選んでいる行のうち、書き換えられるもの（ゴミ箱の中は除く）の Id。</summary>
    private IReadOnlyList<Guid> EditableSelectedIds() => [.. _selected.Where(r => !r.IsInTrash).Select(r => r.Id)];

    /// <summary>1件なら単発（Other）、2件以上なら一括（Bulk・トーストあり）として取り消しに積む。</summary>
    private void Record(int count, TaskMutationResult result, string singleLabel, string bulkLabel, bool toastSingle)
    {
        if (count > 1)
        {
            _undo.Record(result, bulkLabel, UndoKind.Bulk, showToast: true);
        }
        else
        {
            _undo.Record(result, singleLabel, UndoKind.Other, showToast: toastSingle);
        }
    }

    /// <summary>文の主語（1件ならタイトル、それ以外は件数）。</summary>
    private string Subject(IReadOnlyList<Guid> ids) =>
        ids.Count == 1 && FindRow(ids[0]) is { } row ? DisplayText.Quote(row.Title) : Count(ids.Count);

    private static string Count(int count) => string.Create(CultureInfo.InvariantCulture, $"{count} 件");
}

using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Palette;

/// <summary>
/// パレットの実行（INTERFACES 5.8）。画面の切り替え・開くものは閉じてから IPaletteHost（ShellService）に渡す。
/// 書き込みはすべて取り消しを記録する。トーストは UI 設計書 13.1 のとおり（作成・一括・繰り越し・テンプレート展開・繰り返しの完了）。
/// </summary>
public sealed partial class CommandPaletteViewModel
{
    /// <summary>Enter（entry なし＝選んでいる行）・クリック: タスクなら開き、コマンドなら実行する。</summary>
    [RelayCommand]
    public async Task ExecuteAsync(PaletteEntry? entry)
    {
        if (entry is null)
        {
            await EnsureFreshAsync();
            entry = Selected;
        }
        if (entry is null || _busy)
        {
            return;
        }
        _busy = true;
        try
        {
            switch (entry)
            {
                case PaletteTaskItem task:
                    await RememberRecentAsync(task.TaskId);
                    Close();
                    _host.RevealTask(task.TaskId);
                    break;
                case PaletteItem item:
                    await ExecuteItemAsync(item);
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>絞り込み記号を入れる（パレットの下の案内・ヘルプの行から）。絞り込み中なら外す。</summary>
    public async Task InsertPrefixAsync(string symbol)
    {
        Scope = null;
        ReplaceText(symbol);
        await RefreshAsync();
    }

    /// <summary>Ctrl+Enter: 入力した文字でタスクを作る（F-136）。Tab で絞り込んだ中なら、そのプロジェクト／タグを付ける。</summary>
    public async Task CreateFromTextAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            await CreateCoreAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CreateCoreAsync()
    {
        if (!CanCreate)
        {
            return;
        }
        var request = _parser.Parse(Text.Trim()).ToRequest();
        if (Scope is { IsProject: true } project && request.ProjectName is null)
        {
            request = request with { ProjectId = project.Id };
        }
        else if (Scope is { IsProject: false } tag)
        {
            request = request with { TagIds = [tag.Id] };
        }
        var result = await _tasks.AddAsync(request);
        if (result.Created.FirstOrDefault() is not { } created)
        {
            Notice = "タスクを作れませんでした";
            return;
        }
        var label = DisplayText.Quote(created.Title) + "を追加しました";
        if (created.DueAt is { } due)
        {
            label += "（期限 " + DisplayText.DueShort(due, created.DueHasTime, _clock) + "）";
        }
        // パレットで作ったタスクは一覧に出ているとは限らないので、行き先と「元に戻す」をトーストで見せる
        _undo.Record(result, label, UndoKind.Other, showToast: true);
        _logger.LogInformation("パレットからタスクを追加しました {Id}", created.Id);
        Close();
    }

    /// <summary>
    /// Space・チェック: タスクの完了を切り替える（F-135。パレットは閉じない）。
    /// 未完了のサブタスクがある親は、一覧と同じように確認する（F-024）。繰り返しの完了だけトーストで次回を知らせる。
    /// </summary>
    public async Task ToggleTaskAsync(PaletteTaskItem? item)
    {
        item ??= Selected as PaletteTaskItem;
        if (item is null || _busy)
        {
            item?.RefreshCheck();
            return;
        }
        _busy = true;
        try
        {
            var completed = !item.IsClosed;
            IReadOnlyList<Guid> ids = [item.TaskId];
            if (completed)
            {
                if (await SubtaskCompletion.ResolveAsync(_tasks, ids, ids, DisplayText.Quote(item.Title), Confirm) is not { } resolved)
                {
                    item.RefreshCheck();
                    return;
                }
                ids = resolved;
            }
            var result = await _tasks.SetCompletedAsync(ids, completed);
            if (result.Changes.IsEmpty)
            {
                item.RefreshCheck();
                return;
            }
            var next = completed ? result.Created.Select(t => t.DueAt).FirstOrDefault(d => d is not null) : null;
            var label = (completed, result.Created.Count > 0) switch
            {
                (true, true) when next is { } due => "完了しました・次回は " + DisplayText.DateWithWeekday(_clock.ToLocalDate(due)),
                (true, true) => "完了しました・次回を作りました",
                (true, false) => DisplayText.Quote(item.Title) + "を完了しました",
                _ => DisplayText.Quote(item.Title) + "を未完了に戻しました",
            };
            _undo.Record(result, label, UndoKind.Toggle, showToast: completed && result.Created.Count > 0);
            item.IsClosed = completed;
            // 最近開いたタスクは開いたときの写しなので、入力を消して出し直しても状態が合うよう差し替える
            foreach (var fresh in result.Affected)
            {
                var index = _recentTasks.FindIndex(t => t.Id == fresh.Id);
                if (index >= 0)
                {
                    _recentTasks[index] = fresh;
                }
            }
            Notice = label;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ExecuteItemAsync(PaletteItem item)
    {
        var target = item.Target;
        switch (target.Kind)
        {
            case PaletteItemKind.CreateTask:
                await CreateCoreAsync();
                return;
            case PaletteItemKind.More:
                var index = Rows.OfType<PaletteEntry>().ToList().IndexOf(item);
                _expanded.Add(target.Parameter!);
                await RefreshAsync(keepIndex: index);
                return;
            case PaletteItemKind.Prefix:
                await InsertPrefixAsync(target.Parameter!);
                return;
        }

        await RecordUsageAsync(target.Key);
        switch (target.Kind)
        {
            case PaletteItemKind.Project or PaletteItemKind.Tag or PaletteItemKind.View or PaletteItemKind.SavedFilter when target.View is { } view:
                Close();
                _host.NavigateTo(view);
                break;
            case PaletteItemKind.Template when target.EntityId is { } templateId:
                await ExpandTemplateAsync(templateId);
                break;
            case PaletteItemKind.Command:
                await RunCommandAsync(target);
                break;
        }
    }

    private async Task RunCommandAsync(PaletteTarget target)
    {
        switch (target.Command)
        {
            case PaletteCommand.CarryOver:
                await CarryOverAsync();
                break;
            case PaletteCommand.DeleteCompleted:
                await DeleteCompletedAsync();
                break;
            case PaletteCommand.OpenTemplates:
                Close();
                _host.OpenTemplates();
                break;
            case PaletteCommand.FocusMode:
                Close();
                _host.OpenFocusMode();
                break;
            case PaletteCommand.Settings:
                Close();
                _host.OpenSettings(target.Parameter);
                break;
            case PaletteCommand.Shortcuts:
                Close();
                _host.OpenShortcuts();
                break;
            case PaletteCommand.ToggleTheme:
                // パレットは開いたまま（続けて戻せるように）。色はその場で変わる
                Notice = $"テーマを「{_host.ToggleTheme()}」にしました";
                await RefreshAsync(keepKey: target.Key);
                break;
            case PaletteCommand.Backup:
                Notice = _host.CreateBackup();
                break;
            case PaletteCommand.OpenDataFolder:
                Close();
                _host.OpenDataFolder();
                break;
        }
    }

    /// <summary>期限切れの未完了をまとめて今日へ（F-137）。1件も無ければ知らせてパレットは開いたまま。</summary>
    private async Task CarryOverAsync()
    {
        var result = await _tasks.CarryOverOverdueAsync();
        if (result.Changes.IsEmpty)
        {
            Notice = "期限切れのタスクはありません";
            return;
        }
        var count = result.Changes.TasksBefore.Count;
        _undo.Record(result, $"期限切れ {count} 件を今日へ繰り越しました", UndoKind.Bulk, showToast: true);
        _logger.LogInformation("期限切れ {Count} 件を今日へ繰り越しました（パレット）", count);
        Close();
    }

    /// <summary>
    /// 完了済み（完了・中止）をまとめてゴミ箱へ。取り消せるので確認は出さない（UI 設計書 13.2）。
    /// 書き込みは呼び出したスレッドで走り、1万件近いと数秒かかる（1万件のデータで 9,658 件・約5秒）ので、
    /// 先にパレットを閉じてからスレッドプールで消す（リポジトリはスレッドセーフ）。終わったらトーストで知らせる。
    /// </summary>
    private async Task DeleteCompletedAsync()
    {
        var rows = await _tasks.QueryAsync(new TaskQuery { Status = TaskStatusFilter.Done, IncludeSubtasks = false });
        if (rows.Count == 0)
        {
            Notice = "完了済みのタスクはありません";
            return;
        }
        Close();
        IReadOnlyCollection<Guid> ids = [.. rows.Select(r => r.Task.Id)];
        var result = await Task.Run(() => _tasks.SoftDeleteAsync(ids));
        _undo.Record(result, $"完了済みの {rows.Count} 件をゴミ箱に移しました", UndoKind.Bulk, showToast: true);
        _logger.LogInformation("完了済み {Count} 件をゴミ箱に移しました（パレット）", rows.Count);
    }

    /// <summary>+テンプレート名（F-15E）: 基準日は今日、プロジェクト・タグ・過去日の扱いはテンプレートの既定のまま展開する。</summary>
    private async Task ExpandTemplateAsync(Guid templateId)
    {
        if (await _templates.GetWithItemsAsync(templateId) is not { } template)
        {
            Notice = "テンプレートが見つかりませんでした";
            return;
        }
        var plan = _expander.Plan(template.Template, template.Items, new ExpansionOptions { AnchorDate = _clock.LocalToday() });
        if (plan.Tasks.Count == 0)
        {
            Notice = "このテンプレートには項目がありません";
            return;
        }
        var result = await _templates.ExpandAsync(plan);
        _undo.Record(result, $"「{template.Template.Name}」から {plan.Tasks.Count} 件を作りました", UndoKind.Bulk, showToast: true);
        _logger.LogInformation("テンプレート {TemplateId} から {Count} 件を作りました（パレット）", templateId, plan.Tasks.Count);
        Close();
    }
}

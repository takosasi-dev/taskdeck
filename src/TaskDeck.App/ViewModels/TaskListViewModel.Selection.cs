using CommunityToolkit.Mvvm.Input;
using TaskDeck.Core.Queries;

namespace TaskDeck.App.ViewModels;

/// <summary>
/// 一覧の選択（波2-E）: 複数選択（Ctrl+クリック・Shift+クリック・Ctrl+A、S-10）と RevealTask で見せる行の選び方。
/// 選択の集合はここが持ち、ListBox（SelectionMode=Extended）とは View が写し合う:
/// ListBox で選びが変わったら View が SetSelection を呼び、ここで集合を変えたら SelectionSyncRequested で View に入れ直してもらう。
/// </summary>
public sealed partial class TaskListViewModel
{
    /// <summary>選んでいる行（一覧の並び順）。主の行（SelectedItem）を必ず含む。</summary>
    private IReadOnlyList<TaskRowViewModel> _selected = [];

    /// <summary>読み込み中にスケルトンで行を消しても、複数選択のほかの行を覚えておく。</summary>
    private IReadOnlyList<Guid> _otherSelectionDuringLoad = [];

    private IReadOnlyList<Guid> _lastNotifiedIds = [];

    /// <summary>行を見える位置までスクロールしてほしい（RevealTask・複製した行など）。</summary>
    public event EventHandler<TaskRowViewModel>? ScrollIntoViewRequested;

    /// <summary>選択の集合をこちらで変えた（読み直しで行を作り直した・解除した）。View は ListBox の選択を SelectedRows に合わせる。</summary>
    public event EventHandler? SelectionSyncRequested;

    /// <summary>選んでいる行（一覧の並び順）。</summary>
    public IReadOnlyList<TaskRowViewModel> SelectedRows => _selected;

    public int SelectedCount => _selected.Count;

    /// <summary>2件以上選んでいる（一括ツールバーを出し、詳細ペインは「N 件を選択中」にする）。</summary>
    public bool IsMultiSelection => _selected.Count > 1;

    /// <summary>一括ツールバーの件数（「3 件」）。</summary>
    public string SelectionSummary => $"{_selected.Count} 件";

    /// <summary>いまのビューの条件（組み込みビューか保存済みフィルタの条件）。絞り込みパネルの「クリア」はここへ戻す（INTERFACES 5.8）。</summary>
    public TaskQuery ViewQuery => BaseQuery(CurrentView);

    /// <summary>いまの一覧にそのタスクの行があるか。</summary>
    public bool Contains(Guid id) => FindRow(id) is not null;

    /// <summary>
    /// View から: ListBox で選んでいる行（Ctrl+クリック・Shift+矢印・UI Automation の AddToSelection など）。
    /// 読み直しの最中（行を差し替えている間）に来たものは無視する（読み直しの後に選び直すため）。
    /// </summary>
    public void SetSelection(IEnumerable<TaskRowViewModel> rows)
    {
        if (_suppressSelectionEvent)
        {
            return;
        }
        var ordered = InDisplayOrder(rows);
        if (ordered.SequenceEqual(_selected))
        {
            return;
        }
        ReplaceSelection(ordered);
        if (SelectedRow is not { } primary || !ordered.Contains(primary))
        {
            _suppressSelectionEvent = true;
            try
            {
                SelectedItem = ordered.FirstOrDefault();
            }
            finally
            {
                _suppressSelectionEvent = false;
            }
        }
        NotifySelection(dropped: false);
    }

    /// <summary>
    /// Shift+クリック: 主に選んでいる行からその行までのタスク行（見出し・入力行は含めない）。
    /// 主の行を先頭に並べる（ListBox は先頭を SelectedItem にするので、起点が変わらないように）。
    /// </summary>
    public IReadOnlyList<TaskRowViewModel> RangeTo(TaskRowViewModel target)
    {
        var anchor = SelectedRow ?? target;
        var from = Rows.IndexOf(anchor);
        var to = Rows.IndexOf(target);
        if (from < 0 || to < 0)
        {
            return [target];
        }
        var step = from <= to ? 1 : -1;
        var result = new List<TaskRowViewModel>();
        for (var i = from; i != to + step; i += step)
        {
            if (Rows[i] is TaskRowViewModel row)
            {
                result.Add(row);
            }
        }
        return result;
    }

    /// <summary>Ctrl+A: 一覧のタスク行ぜんぶ（主の行を先頭に）。</summary>
    public IReadOnlyList<TaskRowViewModel> AllRowsForSelection()
    {
        var rows = Rows.OfType<TaskRowViewModel>().ToList();
        if (SelectedRow is { } primary && rows.Remove(primary))
        {
            rows.Insert(0, primary);
        }
        return rows;
    }

    /// <summary>選択を解除する（一括ツールバーの「選択を解除」・複数選択中の Esc）。</summary>
    [RelayCommand]
    public void ClearSelection()
    {
        SelectedItem = null;
        SelectionSyncRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// そのタスクが出るビュー（INTERFACES 5.8）: 削除済みは「ゴミ箱」、完了・中止は「完了済み」、未完了は「すべて」。
    /// 見つからなければ null（物理削除された・Id の誤り）。
    /// </summary>
    public async Task<ViewKey?> ViewForTaskAsync(Guid id)
    {
        var task = await _tasks.GetAsync(id);
        if (task is null)
        {
            return null;
        }
        return task.IsDeleted ? ViewKey.Trash : task.IsOpen ? ViewKey.All : ViewKey.Completed;
    }

    /// <summary>その行だけを選び、見える位置までスクロールしてもらう。一覧に無ければ false。</summary>
    public bool Reveal(Guid id)
    {
        if (FindRow(id) is not { } row)
        {
            return false;
        }
        if (IsMultiSelection)
        {
            ClearSelection();
        }
        SelectedItem = row;
        ScrollIntoViewRequested?.Invoke(this, row);
        return true;
    }

    /// <summary>完了済みビューの期間を「すべて」にする（古い完了タスクを見せるとき）。</summary>
    public Task ShowAllPeriodsAsync() => SetPeriodCommand.ExecuteAsync(PeriodOptions[^1]);

    /// <summary>選んでいる行の Id（一覧の並び順）。</summary>
    private IReadOnlyList<Guid> SelectedIds() => [.. _selected.Select(r => r.Id)];

    /// <summary>選択の集合を入れ替え、行の見た目（IsSelected）を合わせる。</summary>
    private void ReplaceSelection(IReadOnlyList<TaskRowViewModel> rows)
    {
        if (rows.SequenceEqual(_selected))
        {
            return;
        }
        var keep = rows.ToHashSet();
        foreach (var old in _selected)
        {
            if (!keep.Contains(old))
            {
                old.IsSelected = false;
            }
        }
        foreach (var row in rows)
        {
            row.IsSelected = true;
        }
        var wasMulti = IsMultiSelection;
        _selected = rows;
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        if (wasMulti != IsMultiSelection)
        {
            OnPropertyChanged(nameof(IsMultiSelection));
        }
    }

    /// <summary>読み直した後に、複数選択のほかの行を新しい行で選び直す（消えたものは外れる）。</summary>
    private void RestoreOtherSelection(IReadOnlyList<Guid> ids)
    {
        if (ids.Count < 2)
        {
            return;
        }
        var wanted = ids.ToHashSet();
        var rows = Rows.OfType<TaskRowViewModel>().Where(r => wanted.Contains(r.Id) || ReferenceEquals(r, SelectedItem)).ToList();
        if (rows.Count == 0)
        {
            return;
        }
        SelectedItem ??= rows[0];
        ReplaceSelection(rows);
    }

    private List<TaskRowViewModel> InDisplayOrder(IEnumerable<TaskRowViewModel> rows)
    {
        var set = rows.ToHashSet();
        return set.Count <= 1 ? [.. set] : [.. Rows.OfType<TaskRowViewModel>().Where(set.Contains)];
    }

    private bool SelectionDiffersFromNotified() =>
        SelectedRow?.Id != _lastNotifiedSelection || !_lastNotifiedIds.SequenceEqual(_selected.Select(r => r.Id));

    private void NotifySelection(bool dropped)
    {
        _lastNotifiedSelection = SelectedRow?.Id;
        _lastNotifiedIds = SelectedIds();
        SelectedTaskChanged?.Invoke(this, new ListSelection(_lastNotifiedSelection, dropped) { TaskIds = _lastNotifiedIds });
    }
}

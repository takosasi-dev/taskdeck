using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Scratch;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Scratch;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.ViewModels;

/// <summary>Esc を押したときに実際に起きたこと（要件 4.1.3 の段階動作）。</summary>
public enum EscapeAction
{
    ClearedSearch,
    ClosedDetailPane,
    HidWindow,
    /// <summary>複数選択を解除した（段階動作のいちばん手前）。</summary>
    ClearedSelection,
}

/// <summary>
/// メインウィンドウ（S-01）の中心。ビューの切替・検索・ショートカット・取り消しトーストをまとめ、
/// 子（サイドバー・一覧・詳細ペイン）へ配る。ShellService の SelectedTaskIds / CurrentListQuery もここで書く。
/// View の型は持たない。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>検索の入力停止を待つ時間（UI 設計書 15.2）。</summary>
    public const int SearchDebounceMs = 150;

    /// <summary>「すべてのタスクから探す」のときの見出し。</summary>
    public const string AllTasksSearchTitle = "すべてのタスク";

    private readonly ISettingsStore _settings;
    private readonly UndoService _undo;
    private readonly ShellService _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Debouncer _searchDebouncer;
    private readonly Debouncer _countsDebouncer;
    private readonly Debouncer _sidebarDebouncer;

    private ViewKey _lastTaskListView = ViewKey.Today;
    private string _savedFilterSignature = "";
    private UndoEntry? _toastEntry;
    private bool _initialized;
    private ViewKey? _pendingView;

    /// <summary>起動時の読み込みを始めたか。始める前（DB の準備と並べてメイン画面を用意しただけの間）のデータの変更は、読み込みで拾うので追わない。</summary>
    private bool _loadStarted;

    public MainViewModel(
        SidebarViewModel sidebar,
        TaskListViewModel list,
        TaskDetailViewModel detail,
        ToastViewModel toast,
        ScratchPaneViewModel scratch,
        ScratchStore scratchStore,
        ISettingsStore settings,
        DataChangeHub hub,
        UndoService undo,
        ShellService shell,
        IUiDispatcher dispatcher,
        ILogger<MainViewModel> logger)
    {
        Sidebar = sidebar;
        List = list;
        Detail = detail;
        Toast = toast;
        Scratch = scratch;
        _settings = settings;
        _undo = undo;
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;
        _searchDebouncer = new Debouncer(ApplySearchAsync);
        _countsDebouncer = new Debouncer(RefreshCountsAsync);
        _sidebarDebouncer = new Debouncer(ReloadSidebarAsync);

        IsSidebarVisible = settings.Current.Window.SidebarVisible;

        Sidebar.ViewSelected += async (_, key) => await SelectViewAsync(key);
        Sidebar.CommandInvoked += (_, id) => OpenExtra(id);
        Sidebar.NoticeRequested += (_, message) => Toast.Show(message, null, "IconInfo");
        Sidebar.TasksDropped += async (_, drop) => await OnSidebarDropAsync(drop);
        List.SelectedTaskChanged += (_, selection) => OnListSelectionChanged(selection);
        List.EmptyActionInvoked += async (_, action) => await OnEmptyActionAsync(action);
        List.RowsReloaded += (_, _) => OnListReloaded();
        List.NoticeRequested += (_, message) => Toast.Show(message, null, "IconInfo");
        List.SubtaskInputRequested += async (_, _) => await OpenSubtaskInputAsync();
        List.PropertyChanged += (_, e) =>
        {
            // ドラッグが終わったら（落とさずにやめたときも）サイドバーの項目の強調を消す
            if (e.PropertyName == nameof(TaskListViewModel.ActiveDrag) && List.ActiveDrag is null)
            {
                Sidebar.ClearDropTargets();
            }
        };
        Detail.DeleteRequested += async (_, task) => await DeleteFromDetailAsync(task.Id, task.Title);
        Toast.ActionInvoked += async (_, _) => await UndoAsync();
        // 使い捨てリスト: 小窓に切り離したら一覧に戻る。増えた・名前が変わった・捨てたらサイドバーを作り直す
        Scratch.MovedToWindow += async (_, _) => await SelectViewAsync(_lastTaskListView);
        scratchStore.Changed += (_, e) => _dispatcher.Post(() => OnScratchChanged(e));

        hub.Changed += (_, e) => _dispatcher.Post(() => OnDataChanged(e));
        _undo.ToastRequested += (_, e) => _dispatcher.Post(() => ShowUndoToast(e.Message));
        _undo.Stack.Changed += (_, _) => _dispatcher.Post(CloseStaleUndoToast);
        _shell.UserNotice += (_, message) => _dispatcher.Post(() => Toast.Show(message, null, "IconWarning"));
        // パレット・トレイ・通知から（ShellService は UI スレッドで呼ぶ約束）
        _shell.NavigateRequested += async (_, key) => await NavigateAsync(key);
        _shell.RevealTaskRequested += async (_, id) => await RevealTaskAsync(id);
        _settings.Changed += (_, _) => _dispatcher.Post(OnSettingsChanged);
    }

    public SidebarViewModel Sidebar { get; }

    public TaskListViewModel List { get; }

    public TaskDetailViewModel Detail { get; }

    public ToastViewModel Toast { get; }

    /// <summary>中央に出す使い捨てリスト（ビューが ViewKind.Scratch のとき）。</summary>
    public ScratchPaneViewModel Scratch { get; }

    /// <summary>ビューの見出し（「今日」「業務改善」「#仕事」）。</summary>
    [ObservableProperty]
    private string _title = "今日";

    /// <summary>見出しの補足（一覧の Subtitle と同じもの）。</summary>
    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private ViewKey _currentView = ViewKey.Today;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    private bool _isDetailPaneOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    private bool _hasSelectedTask;

    /// <summary>ウィンドウが狭い（詳細ペインを自動で隠す）。View が入れる。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    private bool _isNarrow;

    [ObservableProperty]
    private bool _isCalendarMode;

    [ObservableProperty]
    private bool _isStatsMode;

    /// <summary>使い捨てリストを出している（一覧ではないので詳細ペインは出さず、Ctrl+Z でタスクを戻さない）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _isScratchMode;

    [ObservableProperty]
    private bool _isListMode = true;

    /// <summary>詳細ペインに出すタスク（一覧の選択、またはカレンダーの選択。カレンダーとは双方向に結ぶ）。</summary>
    [ObservableProperty]
    private Guid? _selectedTaskId;

    /// <summary>検索欄に入っている文字（IME の変換中の文字も含む。検索にかけるのは確定して入力が止まってから）。</summary>
    [ObservableProperty]
    private string _searchText = "";

    public bool IsDetailVisible => IsDetailPaneOpen && HasSelectedTask && !IsNarrow && !IsScratchMode;

    /// <summary>検索欄にフォーカスしてほしい（Ctrl+F）。</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>一覧の入力行を開いてほしい（Ctrl+N）。</summary>
    public event EventHandler? AddTaskRequested;

    /// <summary>使い捨てリストの「＋ 項目を追加…」にフォーカスしてほしい（使い捨てリストを出している間の Ctrl+N）。</summary>
    public event EventHandler? ScratchAddRequested;

    /// <summary>一覧にキーボードのフォーカスを戻してほしい（Ctrl+1〜4 でビューを変えた後など）。</summary>
    public event EventHandler? ListFocusRequested;

    /// <summary>詳細ペインの「サブタスクを追加」の欄にフォーカスを入れてほしい（行の右クリックメニューから）。</summary>
    public event EventHandler? SubtaskInputFocusRequested;

    /// <summary>行の「サブタスクを追加」: 詳細ペインを開き、選んだ行を読み終えてからサブタスクの欄にフォーカスを入れてもらう。</summary>
    public async Task OpenSubtaskInputAsync()
    {
        IsDetailPaneOpen = true;
        await Detail.LoadAsync(SelectedTaskId);
        SubtaskInputFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 起動時の読み込み。起動時ビューのプロジェクト・使い捨てリストなどが消えていたら「今日」を開く。
    /// 読み込みの途中で NavigateTo が来ていたら（メイン画面を初めて出すのと同時に使い捨てリストを出したときなど）、起動時ビューよりそちらを開く。
    /// </summary>
    public async Task InitializeAsync()
    {
        _loadStarted = true;
        await Sidebar.ReloadAsync();
        _savedFilterSignature = SavedFilterSignature();
        var requested = _pendingView ?? (ViewKey.TryParse(_settings.Current.General.StartView, out var key) ? key : ViewKey.Today);
        _initialized = true;
        await SelectViewAsync(Sidebar.Contains(requested) ? requested : ViewKey.Today);
    }

    public async Task SelectViewAsync(ViewKey key)
    {
        var watch = Stopwatch.StartNew();
        CurrentView = key;
        IsCalendarMode = key.Kind == ViewKind.Calendar;
        IsStatsMode = key.Kind == ViewKind.Stats;
        IsScratchMode = key.Kind == ViewKind.Scratch;
        IsListMode = key.IsTaskList;
        Title = TitleFor(key);
        Sidebar.SetCurrentView(key);
        if (IsScratchMode && key.Id is { } scratchId)
        {
            Scratch.Show(scratchId);
        }
        else
        {
            Scratch.Clear();
        }
        if (key.IsTaskList)
        {
            _lastTaskListView = key;
            List.Title = Title;
            List.Counts = Sidebar.Counts;
            // 詳細ペインに出ているタスクが新しいビューにもあれば選び直す。無ければ詳細ペインを閉じる
            await List.ShowAsync(key, SelectedTaskId);
            if (List.SelectedRow is null)
            {
                SelectedTaskId = null;
            }
            _shell.CurrentListQuery = List.LastQuery;
        }
        else
        {
            _shell.CurrentListQuery = null;
        }
        _logger.LogDebug("ビューを切り替えました: {View} {Milliseconds} ms", key.ToString(), watch.ElapsedMilliseconds);
    }

    /// <summary>検索欄の文字が変わった。IME の変換中は検索にかけず、確定して 150ms 入力が止まったら検索する。</summary>
    public void UpdateSearchText(string text, bool isComposing)
    {
        SearchText = text;
        if (!isComposing)
        {
            _searchDebouncer.Schedule(SearchDebounceMs);
        }
    }

    /// <summary>検索欄の文字で一覧を絞る（入力停止の後に呼ばれる）。カレンダー・振り返りから検索したら一覧に戻る。</summary>
    public async Task ApplySearchAsync()
    {
        var text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        if (text == List.SearchText)
        {
            return;
        }
        if (text is not null && !IsListMode)
        {
            await SelectViewAsync(_lastTaskListView);
        }
        // 「すべてのタスクから探す」にしている間は、語を変えても範囲はそのまま
        var allTasks = text is not null && List.SearchAllTasks;
        List.Title = allTasks ? AllTasksSearchTitle : Title;
        await List.SetSearchAsync(text, allTasks);
        _shell.CurrentListQuery = IsListMode ? List.LastQuery : null;
    }

    public async Task ClearSearchAsync()
    {
        SearchText = "";
        List.Title = Title;
        if (List.SearchText is not null)
        {
            await List.SetSearchAsync(null);
            _shell.CurrentListQuery = IsListMode ? List.LastQuery : null;
        }
    }

    /// <summary>Esc の段階動作（複数選択の解除 → 検索クリア → 詳細ペインを閉じる → ウィンドウを引っ込める）。</summary>
    public async Task<EscapeAction> HandleEscapeAsync()
    {
        if (List.IsMultiSelection)
        {
            List.ClearSelection();
            return EscapeAction.ClearedSelection;
        }
        if (SearchText.Length > 0 || List.SearchText is not null)
        {
            await ClearSearchAsync();
            return EscapeAction.ClearedSearch;
        }
        if (IsDetailVisible)
        {
            IsDetailPaneOpen = false;
            return EscapeAction.ClosedDetailPane;
        }
        _shell.HideMainWindow();
        return EscapeAction.HidWindow;
    }

    /// <summary>
    /// ShellService.NavigateTo（パレット・トレイ・通知から）。前の検索語が残っていると別のビューが絞られたまま出るので、検索は消す。
    /// 消えたプロジェクト・タグ・フィルタへは移らない。
    /// </summary>
    public async Task NavigateAsync(ViewKey key)
    {
        if (!_initialized)
        {
            // 起動時の読み込みが済んでから開く（先に開くと、読み込みの最後の起動時ビューで上書きされる）
            _pendingView = key;
            return;
        }
        if (!Sidebar.Contains(key))
        {
            _logger.LogWarning("サイドバーに無いビューへは移りません: {Kind}", key.Kind);
            return;
        }
        await ClearSearchAsync();
        await SelectViewAsync(key);
        if (key.IsTaskList)
        {
            ListFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// ShellService.RevealTask（パレットの検索結果・通知のクリックから。INTERFACES 5.8）。
    /// いまの一覧にそのタスクが出ていればそこで、出ていなければ出るビュー（未完了は「すべて」、完了・中止は「完了済み」、
    /// 削除済みは「ゴミ箱」）に切り替えて、選んで・見える位置までスクロールし・詳細ペインを開く。
    /// </summary>
    public async Task RevealTaskAsync(Guid taskId)
    {
        if (!IsListMode || !List.Contains(taskId))
        {
            if (await List.ViewForTaskAsync(taskId) is not { } view)
            {
                Toast.Show("タスクが見つかりませんでした", null, "IconWarning");
                return;
            }
            await ClearSearchAsync();
            await SelectViewAsync(view);
            if (!List.Contains(taskId) && view.Kind == ViewKind.Completed)
            {
                // 既定の「過去30日」より前に閉じたもの
                await List.ShowAllPeriodsAsync();
            }
        }
        if (List.Reveal(taskId))
        {
            IsDetailPaneOpen = true;
        }
    }

    [RelayCommand]
    private async Task SelectViewByKeyAsync(string? keyText)
    {
        if (ViewKey.TryParse(keyText, out var key))
        {
            await SelectViewAsync(key);
            if (key.IsTaskList)
            {
                ListFocusRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
        _settings.Update(s => s.Window.SidebarVisible = IsSidebarVisible);
    }

    [RelayCommand]
    private void ToggleDetailPane()
    {
        if (HasSelectedTask)
        {
            IsDetailPaneOpen = !IsDetailPaneOpen;
        }
    }

    [RelayCommand]
    private void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Ctrl+N: 一覧の末尾に入力行を出す（入力行の無い画面からは、出せるビューへ移ってから）。
    /// 使い捨てリストを出している間は、そのリストの「＋ 項目を追加…」へ（小窓に出している間は一覧へ）。
    /// </summary>
    [RelayCommand]
    private async Task BeginAddTaskAsync()
    {
        if (IsScratchMode && Scratch is { List: not null, IsDetached: false })
        {
            ScratchAddRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!IsListMode)
        {
            await SelectViewAsync(_lastTaskListView);
        }
        if (List.SearchText is not null)
        {
            await ClearSearchAsync();
        }
        if (List.IsCompletedView || List.IsTrashView)
        {
            await SelectViewAsync(ViewKey.Today);
        }
        AddTaskRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ctrl+Z・トーストの「元に戻す」。使い捨てリストを出している間の Ctrl+Z は、見えないタスクの操作を戻してしまうので効かせない
    /// （入力欄の中の Ctrl+Z は入力欄の文字を戻す。トーストのボタンはいつでも効く）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoByKey))]
    private async Task UndoAsync()
    {
        Toast.Close();
        if (await _undo.UndoLatestAsync())
        {
            await Detail.RefreshAsync();
        }
    }

    private bool CanUndoByKey() => !IsScratchMode;

    [RelayCommand]
    private void OpenSettings() => _shell.OpenSettings();

    [RelayCommand]
    private void OpenTemplates() => _shell.OpenTemplates();

    [RelayCommand]
    private void OpenCommandPalette() => _shell.OpenCommandPalette();

    [RelayCommand]
    private void OpenShortcuts() => _shell.OpenShortcuts();

    [RelayCommand]
    private void OpenFocusMode() => _shell.OpenFocusMode();

    [RelayCommand]
    private async Task ShowListAsync()
    {
        if (!IsListMode)
        {
            await SelectViewAsync(_lastTaskListView);
        }
    }

    [RelayCommand]
    private async Task ShowCalendarAsync()
    {
        if (!IsCalendarMode)
        {
            await SelectViewAsync(ViewKey.Calendar);
        }
    }

    // ---- 内部 ----

    partial void OnSelectedTaskIdChanged(Guid? value)
    {
        HasSelectedTask = value is not null;
        if (value is not null && _settings.Current.Appearance.AutoShowDetailPane)
        {
            IsDetailPaneOpen = true;
        }
        LoadDetail(value);
    }

    private async void LoadDetail(Guid? id) => await Detail.LoadAsync(id);

    /// <summary>
    /// 一覧の選択が変わった。ShellService には一覧で選んでいる行をぜんぶ入れる（複数選択も INTERFACES 5.8）。
    /// 詳細ペインは主の行（複数選択中は「N 件を選択中」を上に重ねる）。
    /// 読み直しで行が外れただけ（詳細ペインで期限を変えた等）なら、詳細ペインはそのタスクを出し続ける。
    /// </summary>
    private void OnListSelectionChanged(ListSelection selection)
    {
        _shell.SelectedTaskIds = selection.TaskIds;
        if (!selection.DroppedByReload)
        {
            SelectedTaskId = selection.TaskId;
        }
    }

    /// <summary>詳細ペインの「削除」。一覧に出ている行なら次の行を選び直し、出ていなければ詳細ペインを閉じる。</summary>
    private async Task DeleteFromDetailAsync(Guid id, string title)
    {
        var listed = List.SelectedRow?.Id == id;
        await List.DeleteAsync(id, title);
        if (!listed && SelectedTaskId == id)
        {
            SelectedTaskId = null;
        }
    }

    private void OnListReloaded()
    {
        Subtitle = List.Subtitle;
        if (IsListMode)
        {
            _shell.CurrentListQuery = List.LastQuery;
        }
    }

    private async Task OnEmptyActionAsync(EmptyStateAction action)
    {
        switch (action)
        {
            case EmptyStateAction.SearchAllTasks when List.SearchText is { } text:
                // 「すべてのタスクから探す」: 削除済み以外のすべての状態から、同じ語で探し直す
                List.Title = AllTasksSearchTitle;
                await List.SetSearchAsync(text, allTasks: true);
                _shell.CurrentListQuery = List.LastQuery;
                break;
            case EmptyStateAction.ShowUpcoming:
                await SelectViewAsync(ViewKey.Upcoming);
                break;
        }
    }

    /// <summary>サイドバーのプロジェクトに落とした＝そのプロジェクトへ移す、タグに落とした＝そのタグを付ける（UI 設計書 15.5）。</summary>
    private async Task OnSidebarDropAsync(SidebarDrop drop)
    {
        switch (drop.Target)
        {
            case { Kind: NavItemKind.Project, EntityId: { } projectId }:
                await List.MoveToProjectAsync(drop.TaskIds, projectId);
                break;
            case { Kind: NavItemKind.Tag, EntityId: { } tagId }:
                await List.AddTagAsync(drop.TaskIds, tagId, drop.Target.Label);
                break;
        }
    }

    private async Task RefreshCountsAsync()
    {
        await Sidebar.RefreshCountsAsync();
        List.Counts = Sidebar.Counts;
    }

    private async Task ReloadSidebarAsync()
    {
        await Sidebar.ReloadAsync();
        List.Counts = Sidebar.Counts;
        // 開いているプロジェクト・タグの名前が変わったら見出しも変える
        Title = TitleFor(CurrentView);
        if (!List.SearchAllTasks)
        {
            List.Title = Title;
        }
    }

    private void OnDataChanged(DataChangedEventArgs e)
    {
        if (!_loadStarted)
        {
            return;
        }
        var labelsChanged = (e.Kinds & (DataChangeKind.Projects | DataChangeKind.Tags)) != 0;
        var tasksChanged = (e.Kinds & DataChangeKind.Tasks) != 0;
        var templatesChanged = (e.Kinds & DataChangeKind.Templates) != 0;
        if (labelsChanged)
        {
            List.InvalidateLookups();
            _sidebarDebouncer.Schedule(TaskListViewModel.ChangeDebounceMs);
        }
        if (tasksChanged)
        {
            _countsDebouncer.Schedule(TaskListViewModel.ChangeDebounceMs);
        }
        if (templatesChanged)
        {
            // テンプレートの一群の見出しの名前を当て直す（F-15B）
            List.InvalidateTemplates();
        }
        if ((labelsChanged || tasksChanged || templatesChanged) && IsListMode)
        {
            List.ScheduleReload(TaskListViewModel.ChangeDebounceMs);
        }
        if (SelectedTaskId is { } id && (labelsChanged || (tasksChanged && (e.TaskIds.Count == 0 || e.TaskIds.Contains(id)))))
        {
            RefreshDetail();
        }
    }

    private async void RefreshDetail() => await Detail.RefreshAsync();

    /// <summary>使い捨てリストが増えた・名前が変わった・捨てられた。出していたリストが捨てられたら一覧（今日など）へ戻る。</summary>
    private async void OnScratchChanged(ScratchChangedEventArgs e)
    {
        _sidebarDebouncer.Schedule(TaskListViewModel.ChangeDebounceMs);
        if (e.Change == ScratchChange.Discarded && CurrentView == ViewKey.ForScratch(e.ListId))
        {
            await SelectViewAsync(_lastTaskListView);
        }
    }

    /// <summary>保存済みフィルタが増えた・変わったときだけサイドバーを作り直す（ウィンドウ配置の保存などでは作り直さない）。</summary>
    private void OnSettingsChanged()
    {
        var signature = SavedFilterSignature();
        if (signature != _savedFilterSignature)
        {
            _savedFilterSignature = signature;
            _sidebarDebouncer.Schedule(TaskListViewModel.ChangeDebounceMs);
        }
    }

    private string SavedFilterSignature() =>
        string.Join("\n", _settings.Current.SavedFilters.Select(f => f.Id.ToString("N") + ":" + f.Name));

    private void ShowUndoToast(string message)
    {
        _toastEntry = _undo.Stack.Peek();
        var icon = _toastEntry?.Kind switch
        {
            UndoKind.Delete => "IconTrash",
            UndoKind.Toggle => "IconCheck",
            _ => "IconUndo",
        };
        Toast.Show(message, "元に戻す", icon);
    }

    /// <summary>
    /// トーストの「元に戻す」は直前の操作を戻す（UndoService.UndoLatestAsync）。トーストを出した後に別の操作を積んだら、
    /// 押すと別のものが戻ってしまうので、そのトーストは閉じる（戻すのは Ctrl+Z で順に）。
    /// </summary>
    private void CloseStaleUndoToast()
    {
        if (Toast.IsOpen && Toast.HasAction && !ReferenceEquals(_undo.Stack.Peek(), _toastEntry))
        {
            Toast.Close();
        }
    }

    private void OpenExtra(string commandId)
    {
        switch (commandId)
        {
            case "templates":
                _shell.OpenTemplates();
                break;
            case SidebarViewModel.NewScratchCommandId:
                // 最後に使った方（メイン画面か小窓か）で開く。最初は小窓
                Scratch.CreateAndOpen();
                break;
            default:
                _logger.LogWarning("知らないサイドバー項目です: {Id}", commandId);
                break;
        }
    }

    private string TitleFor(ViewKey key) => Sidebar.LabelOf(key) ?? BuiltInViews.TitleOf(key.Kind);
}

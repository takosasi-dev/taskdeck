using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TaskDeck.App.Input;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views.Calendar;
using TaskDeck.App.Views.Main;
using TaskDeck.App.Views.Stats;
using TaskDeck.Core.Settings;
using Wpf.Ui.Controls;

namespace TaskDeck.App.Views;

/// <summary>
/// メインウィンドウ（S-01）。担当: 波1-C。開閉は ShellService が行う（Closing もそちらで処理する）。
/// ここでは、ウィンドウの位置と大きさ・ペインの幅・検索欄の IME・Esc の段階動作だけを受け持つ。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsStore _settings;
    private readonly ShellService _shell;

    private double _sidebarWidth;
    private double _detailWidth;
    private bool _syncingSearch;

    public MainWindow(MainViewModel viewModel, ISettingsStore settings, ShellService shell)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _shell = shell;
        DataContext = viewModel;

        var window = settings.Current.Window;
        _sidebarWidth = Math.Clamp(window.SidebarWidth, (double)FindResource("SidebarMinWidth"), (double)FindResource("SidebarMaxWidth"));
        _detailWidth = Math.Clamp(window.DetailPaneWidth, (double)FindResource("DetailPaneMinWidth"), (double)FindResource("DetailPaneMaxWidth"));
        // 表示する前に位置と大きさを戻す（表示後に動かすと一瞬ずれて見える）
        RestorePlacement(window);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        EnsureModeViews();
        _viewModel.SearchFocusRequested += (_, _) => FocusSearch();
        _viewModel.AddTaskRequested += (_, _) => TaskList.BeginAdd();
        _viewModel.ScratchAddRequested += (_, _) => ScratchPane.FocusAddBox();
        _viewModel.ListFocusRequested += (_, _) => TaskList.FocusList();
        _viewModel.SubtaskInputFocusRequested += (_, _) => EnsureDetailPane().FocusSubtaskInput();
        // 一覧と詳細ペインの「はい／いいえ／キャンセル」の確認（親の完了 F-024・複製 F-016 など）
        _viewModel.List.Confirm = Confirm;
        _viewModel.Detail.Confirm = Confirm;
        ImeGuard.AddCompositionCompletedHandler(SearchBox, OnSearchCompositionCompleted);

        Loaded += OnLoaded;
        // 詳細ペインは起動時に出していないので、最初の描画が済んで手が空いてから作っておく（初めてタスクを選んだときに待たせない）
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => EnsureDetailPane());
    }

    /// <summary>
    /// 詳細ペインの中身は、初めて出すときか起動が落ち着いてからの早い方で作る（大きな XAML で、起動のたびに作ると
    /// メイン画面が出るのが遅れるため。NFR 3.2 の起動2秒）。
    /// </summary>
    private DetailPaneView EnsureDetailPane()
    {
        if (DetailHost.Child is DetailPaneView pane)
        {
            return pane;
        }
        pane = new DetailPaneView { DataContext = _viewModel.Detail };
        // 詳細ペインの「…」は一覧の行の右クリックメニューと同じものを開く
        pane.MoreActionsRequested += async (_, anchor) => await TaskList.OpenRowMenuAsync(anchor);
        DetailHost.Child = pane;
        return pane;
    }

    private ConfirmChoice Confirm(ConfirmRequest request)
    {
        // WPF-UI にも同名の MessageBox 一式があるので、WPF 標準（OS の作法どおりのダイアログ）を名前空間ごと書く
        var answer = System.Windows.MessageBox.Show(
            this,
            request.Message,
            request.Title,
            System.Windows.MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Cancel);
        return answer switch
        {
            System.Windows.MessageBoxResult.Yes => ConfirmChoice.Yes,
            System.Windows.MessageBoxResult.No => ConfirmChoice.No,
            _ => ConfirmChoice.Cancel,
        };
    }

    /// <summary>
    /// 起動時の読み込みを始める（1回だけ。ShellService が窓を出す直前に呼び、読み込みの待ちを窓を出す処理と並べる）。
    /// DB の準備が済んでから呼ぶ。
    /// </summary>
    public Task BeginInitialLoad() => _initialLoad ??= _viewModel.InitializeAsync();

    private Task? _initialLoad;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyPaneLayout();
        UpdateNarrow();
        await BeginInitialLoad();
        TaskList.FocusList();
    }

    // ---- ウィンドウの位置と大きさ ----

    private void RestorePlacement(WindowSettings window)
    {
        Width = Math.Max(window.Width, MinWidth);
        Height = Math.Max(window.Height, MinHeight);
        if (window.Left is { } left && window.Top is { } top && IsOnScreen(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (window.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>画面の外に出ていないか（マルチモニタを含む）。はみ出していれば既定の位置（中央）に戻す。</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var minX = SystemParameters.VirtualScreenLeft;
        var minY = SystemParameters.VirtualScreenTop;
        var maxX = minX + SystemParameters.VirtualScreenWidth;
        var maxY = minY + SystemParameters.VirtualScreenHeight;
        // タイトルバーを掴める範囲（横 80px・縦 40px）が画面に入っていればよい
        return left + 80 < maxX && top + 40 < maxY && left + width - 80 > minX && top > minY - 8;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e) => SavePlacement();

    private void SavePlacement()
    {
        // 一度も出していない窓（起動時に用意だけして、初回起動の3画面の間に終了した等）は位置を持たない（Left/Top が NaN で JSON に書けない）
        if (!IsLoaded)
        {
            return;
        }
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.IsEmpty)
        {
            return;
        }
        _settings.Update(s =>
        {
            s.Window.Left = bounds.Left;
            s.Window.Top = bounds.Top;
            s.Window.Width = bounds.Width;
            s.Window.Height = bounds.Height;
            s.Window.Maximized = WindowState == WindowState.Maximized;
            s.Window.SidebarVisible = _viewModel.IsSidebarVisible;
            s.Window.SidebarWidth = _sidebarWidth;
            s.Window.DetailPaneWidth = _detailWidth;
        });
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateNarrow();

    /// <summary>3ペインが入らない幅（一覧の最小幅 400 を割る）になったら詳細ペインを自動で隠す（UI 設計書 5.1）。</summary>
    private void UpdateNarrow()
    {
        if (ActualWidth <= 0)
        {
            return;
        }
        var needed = (_viewModel.IsSidebarVisible ? _sidebarWidth : 0)
            + (double)FindResource("TaskListMinWidth")
            + _detailWidth
            + 8;
        _viewModel.IsNarrow = ActualWidth < needed;
    }

    // ---- ペインの幅 ----

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsSidebarVisible):
            case nameof(MainViewModel.IsDetailVisible):
                ApplyPaneLayout();
                UpdateNarrow();
                break;
            case nameof(MainViewModel.IsCalendarMode):
            case nameof(MainViewModel.IsStatsMode):
                EnsureModeViews();
                break;
            case nameof(MainViewModel.SearchText) when SearchBox.Text != _viewModel.SearchText:
                // Esc などで ViewModel 側から消したとき
                _syncingSearch = true;
                SearchBox.Text = _viewModel.SearchText;
                _syncingSearch = false;
                break;
        }
    }

    /// <summary>
    /// カレンダーと振り返りは、初めて開いたときに作って以後は使い回す（どちらも大きな XAML で、起動のたびに作ると
    /// メイン画面が出るのが遅れるため。NFR 3.2 の起動2秒）。
    /// </summary>
    private void EnsureModeViews()
    {
        if (_viewModel.IsCalendarMode && CalendarHost.Child is null)
        {
            var calendar = new CalendarView();
            calendar.SetBinding(CalendarView.SelectedTaskIdProperty, new Binding(nameof(MainViewModel.SelectedTaskId)) { Mode = BindingMode.TwoWay });
            CalendarHost.Child = calendar;
        }
        if (_viewModel.IsStatsMode && StatsHost.Child is null)
        {
            StatsHost.Child = new StatsView();
        }
    }

    private void ApplyPaneLayout()
    {
        var sidebar = _viewModel.IsSidebarVisible;
        SidebarColumn.MinWidth = sidebar ? (double)FindResource("SidebarMinWidth") : 0;
        SidebarColumn.Width = sidebar ? new GridLength(_sidebarWidth) : new GridLength(0);
        SidebarSplitter.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        Sidebar.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;

        var detail = _viewModel.IsDetailVisible;
        if (detail)
        {
            EnsureDetailPane();
        }
        DetailColumn.MinWidth = detail ? (double)FindResource("DetailPaneMinWidth") : 0;
        DetailColumn.Width = detail ? new GridLength(_detailWidth) : new GridLength(0);
        DetailSplitter.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        DetailPane.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSidebarSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _sidebarWidth = SidebarColumn.ActualWidth;
        _settings.Update(s => s.Window.SidebarWidth = _sidebarWidth);
        UpdateNarrow();
    }

    private void OnDetailSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _detailWidth = DetailColumn.ActualWidth;
        _settings.Update(s => s.Window.DetailPaneWidth = _detailWidth);
        UpdateNarrow();
    }

    // ---- 検索 ----

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_syncingSearch)
        {
            _viewModel.UpdateSearchText(SearchBox.Text, ImeGuard.IsComposing(SearchBox));
        }
    }

    /// <summary>変換が確定したときだけ検索を走らせる（CLAUDE.md 1.7）。</summary>
    private void OnSearchCompositionCompleted(object sender, RoutedEventArgs e) =>
        _viewModel.UpdateSearchText(SearchBox.Text, isComposing: false);

    // ---- キー操作 ----

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // 開いているポップアップ（並び替え・ピッカーなど）があれば、まずそれを閉じる
            if (TaskList.CloseOpenPopups() | (DetailHost.Child is DetailPaneView pane && pane.CloseOpenPopups()))
            {
                return;
            }
            var action = await _viewModel.HandleEscapeAsync();
            if (action == EscapeAction.ClearedSearch && SearchBox.IsKeyboardFocusWithin)
            {
                TaskList.FocusList();
            }
            return;
        }
        // 「?」はショートカット一覧（入力欄にフォーカスが無いときだけ）
        if (e.Key == Key.OemQuestion
            && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
            && Keyboard.FocusedElement is not TextBoxBase)
        {
            e.Handled = true;
            _shell.OpenShortcuts();
        }
    }

    // ---- ドラッグ中のゴースト（UI 設計書 15.5） ----

    /// <summary>タスクを運んでいる間、ゴーストをマウスに付けて動かす（一覧の外・サイドバーの上でも）。</summary>
    private void OnWindowPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(TaskDragPayload)))
        {
            return;
        }
        var p = e.GetPosition(DragLayer);
        Canvas.SetLeft(DragGhost, p.X + 12);
        Canvas.SetTop(DragGhost, p.Y + 4);
    }

    /// <summary>落とす先でない場所（一覧とサイドバーのプロジェクト・タグ以外）: 置けない印にして、行き先の文言を消す。</summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TaskDragPayload)) is TaskDragPayload payload)
        {
            e.Effects = DragDropEffects.None;
            payload.Hint = null;
            e.Handled = true;
        }
    }
}

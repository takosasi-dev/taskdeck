using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Input;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Queries;

namespace TaskDeck.App.Views.Main;

/// <summary>
/// 中央の一覧（S-01）。担当: 波1-C、波2-E。
/// キー操作（Space・Enter/F2・Delete・Ctrl+↑↓・Tab）と、取り消せない操作の確認、
/// 読み直し（Reset）をまたいだスクロール位置とキーボードのフォーカスの維持をここで受け持つ。
/// 複数選択・一括ツールバー・値を選ぶポップアップは TaskListView.Selection.cs。
/// </summary>
public partial class TaskListView : UserControl
{
    private TaskListViewModel? _boundModel;
    private double _savedOffset;
    private bool _listHadFocus;
    private bool _addEditorHadFocus;
    private ViewKey? _lastView;

    public TaskListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // 絞り込みパネル（中身は波2-G）。条件を受け取ったら一覧に反映する
        FilterPanelHost.Applied += OnFilterApplied;
        FilterPanelHost.Cancelled += (_, _) => FilterPopup.IsOpen = false;
        RowList.IsTextSearchEnabled = false;
        WirePickers();
    }

    private TaskListViewModel? ViewModel => DataContext as TaskListViewModel;

    private static TaskRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TaskRowViewModel;

    /// <summary>入力行までスクロールしてフォーカスを入れる（Ctrl+N）。</summary>
    public void BeginAdd() => FocusAddEditor();

    /// <summary>一覧にキーボードのフォーカスを入れる（選択中の行、なければ最初の行）。</summary>
    public void FocusList() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ViewModel is not { } vm)
            {
                return;
            }
            var target = (object?)vm.SelectedRow ?? vm.Rows.OfType<TaskRowViewModel>().FirstOrDefault();
            if (target is not null && RowList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem item)
            {
                item.Focus();
                return;
            }
            RowList.Focus();
        });

    /// <summary>開いているポップアップを閉じる（Esc）。閉じたものがあれば true。</summary>
    public bool CloseOpenPopups()
    {
        var closed = false;
        foreach (var popup in new[] { SortPopup, FilterPopup, DuePopup, PriorityPopup, ProjectPopup, TagPopup })
        {
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                closed = true;
            }
        }
        return closed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundModel is not null)
        {
            _boundModel.RowsReloading -= OnRowsReloading;
            _boundModel.RowsReloaded -= OnRowsReloaded;
            _boundModel.ScrollIntoViewRequested -= OnScrollIntoViewRequested;
            _boundModel.SelectionSyncRequested -= OnSelectionSyncRequested;
        }
        _boundModel = ViewModel;
        if (_boundModel is not null)
        {
            _boundModel.RowsReloading += OnRowsReloading;
            _boundModel.RowsReloaded += OnRowsReloaded;
            _boundModel.ScrollIntoViewRequested += OnScrollIntoViewRequested;
            _boundModel.SelectionSyncRequested += OnSelectionSyncRequested;
        }
    }

    /// <summary>RevealTask などで選んだ行を見える位置に出し、キーボードのフォーカスもそこへ移す。</summary>
    private void OnScrollIntoViewRequested(object? sender, TaskRowViewModel row)
    {
        RowList.ScrollIntoView(row);
        FocusList();
    }

    // ---- 読み直しをまたいで、スクロール位置とフォーカスを保つ ----

    private void OnRowsReloading(object? sender, EventArgs e)
    {
        _savedOffset = FindScrollViewer()?.VerticalOffset ?? 0;
        _listHadFocus = RowList.IsKeyboardFocusWithin;
        _addEditorHadFocus = FindAddEditor()?.IsKeyboardFocusWithin == true;
    }

    private void OnRowsReloaded(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        var viewChanged = _lastView != vm.CurrentView;
        _lastView = vm.CurrentView;
        var offset = viewChanged ? 0 : _savedOffset;
        var refocusList = _listHadFocus;
        var refocusAdd = _addEditorHadFocus;
        var render = Stopwatch.StartNew();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            FindScrollViewer()?.ScrollToVerticalOffset(offset);
            if (refocusAdd)
            {
                FocusAddEditor();
            }
            else if (refocusList)
            {
                FocusList();
            }
            // 行を差し替えてから、見えている行の配置と描画の準備が済むまで（仮想化が効いていれば件数によらず短い）
            if (AppServices.IsReady)
            {
                AppServices.Get<ILogger<TaskListView>>().LogDebug(
                    "一覧の再描画: {Rows}行 {Milliseconds} ms", vm.Rows.Count, render.ElapsedMilliseconds);
            }
        });
    }

    private ScrollViewer? FindScrollViewer() => FindDescendant<ScrollViewer>(RowList);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                return found;
            }
            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    /// <summary>入力行のテキスト欄（入れ物ができていなければ null）。</summary>
    private TextBox? FindAddEditor()
    {
        if (ViewModel is not { } vm || RowList.ItemContainerGenerator.ContainerFromItem(vm.AddRow) is not ListBoxItem item)
        {
            return null;
        }
        return FindDescendant<TextBox>(item);
    }

    /// <summary>入力行までスクロールして、テキスト欄にフォーカスを入れる（入れ物ができるのを待ってから）。</summary>
    private void FocusAddEditor()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        RowList.ScrollIntoView(vm.AddRow);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            RowList.ScrollIntoView(vm.AddRow);
            if (FindAddEditor() is { IsVisible: true } box)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
        });
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.OriginalSource is TextBoxBase)
        {
            return;
        }
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.Space when !ctrl && vm.SelectedRow is not null:
                e.Handled = true;
                await vm.ToggleCompleteCommand.ExecuteAsync(vm.SelectedRow);
                break;
            case Key.Enter when !ctrl && vm.SelectedRow is not null:
            case Key.F2 when vm.SelectedRow is not null:
                e.Handled = true;
                vm.BeginRenameCommand.Execute(vm.SelectedRow);
                break;
            // 一覧のタスク行をぜんぶ選ぶ（ListBox の Ctrl+A は見出しと入力行まで選ぶので、ここで先に受ける）
            case Key.A when ctrl:
                e.Handled = true;
                RowList.SelectRows(vm.AllRowsForSelection());
                break;
            // 複製（F-016）。サブタスクがあれば含めるかを聞く
            case Key.D when ctrl && vm.SelectedRow is not null:
                e.Handled = true;
                await vm.DuplicateAsync(vm.SelectedRow);
                break;
            case Key.Delete when !ctrl && vm.SelectedRow is not null:
                e.Handled = true;
                await vm.DeleteSelectedCommand.ExecuteAsync(null);
                break;
            case Key.Up when ctrl:
                e.Handled = true;
                await vm.MoveSelectedAsync(up: true);
                break;
            case Key.Down when ctrl:
                e.Handled = true;
                await vm.MoveSelectedAsync(up: false);
                break;
            // 階層の上げ下げ（F-025）。階層を変えられないとき（行を選んでいない・複数選択・検索結果・完了済み・ゴミ箱）は普通のフォーカス移動に任せる
            case Key.Tab when !ctrl && vm.CanRestructureSelection:
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    await vm.OutdentSelectedAsync();
                }
                else
                {
                    await vm.IndentSelectedAsync();
                }
                break;
        }
    }

    /// <summary>
    /// チェック（クリック・Space・UI Automation の Toggle のどれでも来る）。
    /// バインディングで値が入っただけのとき（行の再利用・読み直し）は、行の状態と同じなので何もしない。
    /// 親の完了の確認（F-024）でキャンセルしたら、チェックを元に戻す（SetCurrentValue なのでバインディングは残る）。
    /// </summary>
    private async void OnRowChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || RowOf(sender) is not { } row || ViewModel is not { } vm)
        {
            return;
        }
        var isChecked = box.IsChecked == true;
        if (isChecked != row.IsClosed && !await vm.SetCompletedAsync(row, isChecked) && ReferenceEquals(box.DataContext, row))
        {
            box.SetCurrentValue(ToggleButton.IsCheckedProperty, row.IsClosed);
        }
    }

    // ---- その場で名前を変える ----

    private async void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || RowOf(sender) is not { } row || ViewModel is not { } vm)
        {
            return;
        }
        if (ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.CommitRenameAsync(row);
            FocusList();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            TaskListViewModel.CancelRename(row);
            FocusList();
        }
    }

    private async void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { } vm && RowOf(sender) is { IsEditing: true } row)
        {
            await vm.CommitRenameAsync(row);
        }
    }

    // ---- 入力行 ----

    private async void OnAddSubmitClick(object sender, RoutedEventArgs e) => await SubmitAddAsync();

    private async void OnAddRowKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ViewModel is not { } vm)
        {
            return;
        }
        if (ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitAddAsync();
        }
        else if (e.Key == Key.Escape)
        {
            // 入力をやめる（書きかけは消す）。一覧にフォーカスを戻す
            e.Handled = true;
            vm.AddRow.Text = "";
            FocusList();
        }
    }

    /// <summary>入力を登録して、入力行に残る（続けて打てるように）。登録できなかったら文字を戻す。</summary>
    private async Task SubmitAddAsync()
    {
        if (ViewModel is not { } vm || !vm.AddRow.HasText)
        {
            return;
        }
        var text = vm.AddRow.Text;
        vm.AddRow.Text = "";
        if (!await vm.AddFromInputAsync(text))
        {
            vm.AddRow.Text = text;
        }
        FocusAddEditor();
    }

    // ---- ゴミ箱 ----

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && RowOf(sender) is { } row)
        {
            await vm.RestoreCommand.ExecuteAsync(row);
        }
    }

    private async void OnPurgeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || RowOf(sender) is not { } row)
        {
            return;
        }
        // 取り消せないので確認する（F-144）
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"{DisplayText.Quote(row.Title)}を完全に削除しますか？\nこの操作は取り消せません。",
            "完全に削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            await vm.PurgeAsync(row);
        }
    }

    private async void OnPurgeAllClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            "ゴミ箱のタスクをすべて完全に削除しますか？\nこの操作は取り消せません。",
            "ゴミ箱を空にする",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            await vm.PurgeAllAsync();
        }
    }

    // ---- 見出しの操作 ----

    private void OnSortClick(object sender, RoutedEventArgs e) => SortPopup.IsOpen = !SortPopup.IsOpen;

    private void OnSortChoiceClick(object sender, RoutedEventArgs e) => SortPopup.IsOpen = false;

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is Popup popup)
        {
            e.Handled = true;
            popup.IsOpen = false;
        }
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            // 「クリア」の戻り先はビューの条件（今日なら期限「今日まで」、プロジェクトならそのプロジェクト）
            FilterPanelHost.BaseQuery = vm.ViewQuery;
            FilterPanelHost.Query = vm.BuildQuery();
        }
        FilterPopup.IsOpen = true;
    }

    private async void OnFilterApplied(object? sender, Controls.FilterAppliedEventArgs e)
    {
        FilterPopup.IsOpen = false;
        if (ViewModel is { } vm)
        {
            await vm.ApplyFilterAsync(e.Query);
        }
    }

    private void OnFocusModeClick(object sender, RoutedEventArgs e) => AppServices.Get<ShellService>().OpenFocusMode();
}

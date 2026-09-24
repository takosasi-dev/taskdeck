using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskDeck.App.Input;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;

namespace TaskDeck.App.Views.Main;

/// <summary>
/// 一覧の複数選択（S-10）と、値を選ぶポップアップ（一括ツールバー・行の右クリックメニューで共用）。担当: 波2-E。
/// 選択は ListBox（Extended）と ViewModel で写し合う: ListBox で変わったら SetSelection、ViewModel が変えたら SelectionSyncRequested。
/// Shift+クリックと Ctrl+A は、見出しと入力行を含めないよう ViewModel に範囲を決めてもらう。
/// </summary>
public partial class TaskListView
{
    /// <summary>ViewModel の選択を ListBox に写している最中（その SelectionChanged を ViewModel に返さない）。</summary>
    private bool _syncingSelection;

    private void WirePickers()
    {
        DuePicker.Picked += async (_, e) =>
        {
            DuePopup.IsOpen = false;
            if (ViewModel is { } vm)
            {
                await vm.SetDueForSelectedAsync(e.Date, e.Time);
            }
        };
        DuePicker.Cancelled += (_, _) => DuePopup.IsOpen = false;
        PriorityPicker.Picked += async (_, e) =>
        {
            PriorityPopup.IsOpen = false;
            if (ViewModel is { } vm)
            {
                await vm.SetPriorityForSelectedAsync(e.Priority);
            }
        };
        PriorityPicker.Cancelled += (_, _) => PriorityPopup.IsOpen = false;
        ProjectPicker.Picked += async (_, e) =>
        {
            ProjectPopup.IsOpen = false;
            if (ViewModel is { } vm)
            {
                await vm.MoveSelectedToProjectAsync(e.ProjectId);
            }
        };
        ProjectPicker.Cancelled += (_, _) => ProjectPopup.IsOpen = false;
    }

    // ---- 選択を写し合う ----

    private void OnSelectionSyncRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        _syncingSelection = true;
        try
        {
            RowList.SelectRows(vm.SelectedRows);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>
    /// 見出し・入力行は選ばせない（Shift+矢印などで入ったものは外し、見出しだけを押したときは直前の選択に戻す）。
    /// 残った行を ViewModel に渡す（ShellService.SelectedTaskIds・一括ツールバー・詳細ペインが拾う）。
    /// </summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var plain = RowList.SelectedItems.OfType<ListItemViewModel>().Where(i => !i.IsSelectable).ToList();
        if (plain.Count > 0)
        {
            var restore = plain.Count == RowList.SelectedItems.Count ? e.RemovedItems.OfType<TaskRowViewModel>().ToList() : [];
            foreach (var item in plain)
            {
                RowList.SelectedItems.Remove(item);
            }
            foreach (var row in restore)
            {
                RowList.SelectedItems.Add(row);
            }
            return;
        }
        if (!_syncingSelection && ViewModel is { } vm)
        {
            vm.SetSelection(RowList.SelectedItems.OfType<TaskRowViewModel>());
        }
    }

    /// <summary>
    /// 行を押した（行の中のボタン・チェック・入力欄は除く）。
    /// Shift+クリック: 主に選んでいる行からその行まで（見出し・入力行を除く）を選ぶ。
    /// それ以外はドラッグの始まりとして覚える。複数選択の中の行を修飾キーなしで押したときは、選択を崩さずに運べるよう
    /// 選び直しを離したときまで待つ（ListBox は押した瞬間にその1行だけの選択にしてしまうため）。
    /// </summary>
    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pressedRow = null;
        _selectOnRelease = false;
        if (ViewModel is not { } vm || RowAt(e.OriginalSource as DependencyObject) is not { } row)
        {
            return;
        }
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != 0 && (modifiers & ModifierKeys.Control) == 0 && vm.SelectedRow is not null)
        {
            e.Handled = true;
            RowList.SelectRows(vm.RangeTo(row));
            (RowList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
            return;
        }
        if (modifiers != ModifierKeys.None)
        {
            return;
        }
        _dragStart = e.GetPosition(RowList);
        _pressedRow = row;
        if (vm.IsMultiSelection && vm.SelectedRows.Contains(row))
        {
            e.Handled = true;
            _selectOnRelease = true;
            (RowList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
        }
    }

    /// <summary>押した場所のタスク行（行の中のボタン・チェック・入力欄を押したときは null）。</summary>
    private static TaskRowViewModel? RowAt(DependencyObject? source)
    {
        var node = source;
        while (node is not null)
        {
            switch (node)
            {
                case ButtonBase or TextBoxBase:
                    return null;
                case ListBoxItem item:
                    return item.DataContext as TaskRowViewModel;
            }
            // 強調した検索語（Run）などビジュアルでないものは論理ツリーで親へ
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // ---- 値を選ぶポップアップ ----

    private void OnBulkDueClick(object sender, RoutedEventArgs e)
    {
        DuePicker.Date = null;
        DuePicker.Time = null;
        OpenAbove(DuePopup, sender);
    }

    private void OnBulkPriorityClick(object sender, RoutedEventArgs e)
    {
        PriorityPicker.Priority = Priority.None;
        OpenAbove(PriorityPopup, sender);
    }

    private void OnBulkProjectClick(object sender, RoutedEventArgs e)
    {
        ProjectPicker.ProjectId = null;
        OpenAbove(ProjectPopup, sender);
    }

    private async void OnBulkTagClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        TagNameBox.Clear();
        await vm.LoadTagChoicesAsync();
        OpenAbove(TagPopup, sender);
        FocusWhenActive(TagNameBox);
    }

    private async void OnTagChoiceClick(object sender, RoutedEventArgs e)
    {
        TagPopup.IsOpen = false;
        if (ViewModel is { } vm && (sender as FrameworkElement)?.DataContext is TagChipViewModel tag)
        {
            await vm.AddTagToSelectedAsync(tag.Id, tag.Name);
        }
    }

    /// <summary>タグの名前を打って Enter（変換確定の Enter では付けない）。無ければ作って付ける。</summary>
    private async void OnTagNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ViewModel is not { } vm || ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        if (e.Key == Key.Enter && box.Text.Trim().Length > 0)
        {
            e.Handled = true;
            var name = box.Text;
            TagPopup.IsOpen = false;
            await vm.AddTagByNameToSelectedAsync(name);
        }
    }

    /// <summary>一括ツールバーから: ボタンの上に開く（パネルの影の余白 左右10・下14 のぶん寄せる）。</summary>
    private void OpenAbove(Popup popup, object target)
    {
        CloseOpenPopups();
        popup.PlacementTarget = target as UIElement;
        popup.Placement = PlacementMode.Top;
        popup.HorizontalOffset = -10;
        popup.VerticalOffset = 10;
        popup.IsOpen = true;
    }

    /// <summary>行から（右クリックメニュー）: 行の下に開く（影の余白 左右10・上8 のぶん寄せる）。</summary>
    private void OpenBelow(Popup popup, UIElement target)
    {
        CloseOpenPopups();
        popup.PlacementTarget = target;
        popup.Placement = PlacementMode.Bottom;
        popup.HorizontalOffset = -10;
        popup.VerticalOffset = -8;
        popup.IsOpen = true;
    }

    /// <summary>
    /// 窓が前面のときだけフォーカスを入れる（裏にある窓の Popup にフォーカスを移すと、開いた直後に閉じてしまう。INTERFACES 6.1）。
    /// </summary>
    private void FocusWhenActive(UIElement element) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Window.GetWindow(this) is { IsActive: true })
            {
                element.Focus();
            }
        });
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TaskDeck.App.Controls.Pickers;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;

namespace TaskDeck.App.Views.Main;

/// <summary>
/// 行の右クリックメニュー（担当: 波2-E）。完了・名前を変更・期限・優先度・プロジェクトへ移動・複製・サブタスクを追加・
/// テンプレートにする・削除。右クリック・Shift+F10 のほか、詳細ペインの「…」からも開く（UI Automation でも開けるように）。
/// 複数選択中の行で開いたときは、完了・期限・優先度・プロジェクト・削除が選んだ行ぜんぶに効く（名前・複製・サブタスクは1件の操作なので出さない）。
/// </summary>
public partial class TaskListView
{
    /// <summary>メニューを開いた行。</summary>
    private TaskRowViewModel? _menuRow;

    /// <summary>詳細ペインの「…」から: いま選んでいる行のメニューを、押したボタンの下に開く。</summary>
    public async Task OpenRowMenuAsync(UIElement anchor)
    {
        if (ViewModel is not { SelectedRow: { IsInTrash: false } row } vm)
        {
            return;
        }
        PrepareRowMenu(vm, row);
        RowMenu.PlacementTarget = anchor;
        RowMenu.Placement = PlacementMode.Bottom;
        RowMenu.IsOpen = true;
        await vm.LoadProjectChoicesAsync();
    }

    /// <summary>右クリック・Shift+F10: 押した行（キーなら選んでいる行）のメニュー。見出し・入力行・何もない所・ゴミ箱の行では出さない。</summary>
    private async void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ViewModel is not { } vm || RowIn(e.OriginalSource as DependencyObject) is not { IsInTrash: false } row)
        {
            e.Handled = true;
            return;
        }
        // 詳細ペインの「…」で開いたときの置き場所を残さない（右クリックはマウスの位置に出す）
        RowMenu.ClearValue(ContextMenu.PlacementTargetProperty);
        RowMenu.ClearValue(ContextMenu.PlacementProperty);
        PrepareRowMenu(vm, row);
        await vm.LoadProjectChoicesAsync();
    }

    private void PrepareRowMenu(TaskListViewModel vm, TaskRowViewModel row)
    {
        _menuRow = row;
        RowMenu.DataContext = vm;
        var multi = IsInMultiSelection(vm, row);
        RowMenuComplete.Header = multi ? "選んだタスクを完了にする" : row.IsClosed ? "未完了に戻す" : "完了にする";
        RowMenuRename.IsEnabled = !multi;
        RowMenuDuplicate.IsEnabled = !multi;
        RowMenuAddSubtask.IsEnabled = !multi && TaskListViewModel.CanAddSubtask(row);
        // 「来週」が何日かで迷わせないよう、実日付を添える（UI 設計書 12.1 と同じ考え）
        var today = vm.Today;
        RowMenuDueToday.Header = $"今日（{QuickDates.Label(today)}）";
        RowMenuDueTomorrow.Header = $"明日（{QuickDates.Label(today.AddDays(1))}）";
        RowMenuDueNextWeek.Header = $"来週（{QuickDates.Label(QuickDates.NextWeek(today))}）";
    }

    private static bool IsInMultiSelection(TaskListViewModel vm, TaskRowViewModel row) =>
        vm.IsMultiSelection && vm.SelectedRows.Contains(row);

    private async void OnRowMenuComplete(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || _menuRow is not { } row)
        {
            return;
        }
        if (IsInMultiSelection(vm, row))
        {
            await vm.CompleteSelectedCommand.ExecuteAsync(null);
        }
        else
        {
            await vm.SetCompletedAsync(row, !row.IsClosed);
        }
    }

    private void OnRowMenuRename(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && _menuRow is { } row)
        {
            vm.BeginRenameCommand.Execute(row);
        }
    }

    private async void OnRowMenuDue(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        var today = vm.Today;
        DateOnly? date = (sender as FrameworkElement)?.Tag switch
        {
            "today" => today,
            "tomorrow" => today.AddDays(1),
            "nextweek" => QuickDates.NextWeek(today),
            _ => null,
        };
        await vm.SetDueForSelectedAsync(date, null);
    }

    /// <summary>「日付を選ぶ…」: メニューが閉じてから、行の下に日付ピッカーを開く。</summary>
    private void OnRowMenuPickDue(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || _menuRow is not { } row)
        {
            return;
        }
        var (date, time) = vm.DueOf(row);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            DuePicker.Date = date;
            DuePicker.Time = time;
            OpenBelow(DuePopup, ContainerOf(row));
        });
    }

    private async void OnRowMenuPriority(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && (sender as FrameworkElement)?.Tag is string name && Enum.TryParse<Priority>(name, out var priority))
        {
            await vm.SetPriorityForSelectedAsync(priority);
        }
    }

    /// <summary>「プロジェクトへ移動」の下の項目（行き先ごとに作った MenuItem の Click がここまで上がってくる）。</summary>
    private async void OnRowMenuProject(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && (e.OriginalSource as FrameworkElement)?.DataContext is MoveTarget target)
        {
            await vm.MoveSelectedToProjectAsync(target.Id);
        }
    }

    private async void OnRowMenuDuplicate(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && _menuRow is { } row)
        {
            await vm.DuplicateAsync(row);
        }
    }

    private void OnRowMenuAddSubtask(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && _menuRow is { } row)
        {
            vm.RequestSubtaskInput(row);
        }
    }

    /// <summary>「テンプレートにする」: テンプレート画面を開く（そこで「選択中のタスクから作る」を押してもらう。選択は ShellService に入っている）。</summary>
    private void OnRowMenuTemplate(object sender, RoutedEventArgs e) => AppServices.Get<ShellService>().OpenTemplates();

    private async void OnRowMenuDelete(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.DeleteSelectedCommand.ExecuteAsync(null);
        }
    }

    // ---- テンプレートの一群の見出し（F-15B） ----

    private async void OnBatchCompleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && (sender as FrameworkElement)?.DataContext is TemplateBatchHeaderViewModel header)
        {
            await vm.CompleteBatchAsync(header);
        }
    }

    private async void OnBatchDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && (sender as FrameworkElement)?.DataContext is TemplateBatchHeaderViewModel header)
        {
            await vm.DeleteBatchAsync(header);
        }
    }

    /// <summary>行の入れ物（まだ作られていなければ一覧そのもの）。ポップアップの置き場所に使う。</summary>
    private UIElement ContainerOf(TaskRowViewModel row) =>
        RowList.ItemContainerGenerator.ContainerFromItem(row) as UIElement ?? RowList;

    /// <summary>その場所を含むタスク行（行の中のどこでも）。</summary>
    private static TaskRowViewModel? RowIn(DependencyObject? source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is ListBoxItem item)
            {
                return item.DataContext as TaskRowViewModel;
            }
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskDeck.App.Input;
using TaskDeck.App.ViewModels;

namespace TaskDeck.App.Views.Main;

/// <summary>
/// 左のサイドバー（UI 設計書 6.5）。担当: 波1-C、波2-E。
/// 名前の入力（追加・改名）と、取り消せない削除の確認と、一覧から運んできたタスクの受け取りをここで受ける。
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

    private SidebarViewModel? ViewModel => DataContext as SidebarViewModel;

    private static NavItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as NavItemViewModel;

    // ---- 一覧から運んできたタスク（プロジェクトへ移す・タグを付ける） ----

    private void OnNavDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TaskDragPayload)) is not TaskDragPayload payload)
        {
            return;
        }
        e.Handled = true;
        if (ItemOf(sender) is not { AcceptsTasks: true } item)
        {
            e.Effects = DragDropEffects.None;
            payload.Hint = null;
            return;
        }
        e.Effects = DragDropEffects.Move;
        item.IsDropTarget = true;
        payload.Hint = item.DropHint;
    }

    /// <summary>項目の中の部品の間を動くたびにも来るので、項目の外へ出たときだけ戻す。</summary>
    private void OnNavDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element || ItemOf(sender) is not { } item)
        {
            return;
        }
        var p = e.GetPosition(element);
        if (p.X < 0 || p.Y < 0 || p.X >= element.ActualWidth || p.Y >= element.ActualHeight)
        {
            item.IsDropTarget = false;
        }
    }

    private void OnNavDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TaskDragPayload)) is not TaskDragPayload payload || ItemOf(sender) is not { } item || ViewModel is not { } vm)
        {
            return;
        }
        e.Handled = true;
        vm.DropTasks(item, payload.TaskIds);
    }

    private async void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ItemOf(sender) is not { } item || ViewModel is not { } vm)
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
            await vm.CommitEditAsync(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            item.IsEditing = false;
        }
    }

    private async void OnEditLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ItemOf(sender) is { IsEditing: true } item && ViewModel is { } vm)
        {
            await vm.CommitEditAsync(item);
        }
    }

    private void OnDeleteProject(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }
        // 取り消せない操作なので確認する（F-144）。所属タスクは消えない
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"プロジェクト「{item.Label}」を削除しますか？\n所属しているタスクは「プロジェクトなし」に移ります（タスクは消えません）。",
            "プロジェクトを削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            item.DeleteCommand?.Execute(item);
        }
    }

    private void OnDeleteTag(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"タグ「{item.Label}」を削除しますか？\nタスクからは外れますが、タスクは消えません。",
            "タグを削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            item.DeleteCommand?.Execute(item);
        }
    }

    /// <summary>使っていないタグをまとめて削除（F-045）。取り消せないので確認する。</summary>
    private async void OnDeleteUnusedTags(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            "どのタスクにも付いていないタグをまとめて削除しますか？\nタスクは変わりません。",
            "使っていないタグを削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            await vm.DeleteUnusedTagsCommand.ExecuteAsync(null);
        }
    }
}

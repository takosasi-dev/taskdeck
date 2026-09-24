using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TaskDeck.App.ViewModels;

namespace TaskDeck.App.Views.Main;

/// <summary>
/// 一覧のドラッグ＆ドロップ（担当: 波2-E、UI 設計書 15.5）。
/// 行の上端 1/4 に落とす＝その行の前、下端 1/4＝後ろ（並び替え。手動順のときだけ）、真ん中＝その行の子にする。
/// 前後は 2px のアクセント線と丸点、子にする先はアクセントの枠で示す。掴んだ行は元の位置に薄く残し、
/// マウスに付いて動くゴースト（MainWindow）の右端に「N 番目へ」などを出す。一覧の上下の端では少しずつスクロールする。
/// サイドバーのプロジェクト・タグへ落とすのは SidebarView が受ける。
/// </summary>
public partial class TaskListView
{
    private Point? _dragStart;
    private TaskRowViewModel? _pressedRow;
    private bool _selectOnRelease;
    private TaskRowViewModel? _dropTargetRow;

    /// <summary>押したまま少し動かしたらドラッグを始める（Windows の既定のしきい値）。</summary>
    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _pressedRow is not { } row || ViewModel is not { } vm)
        {
            return;
        }
        var now = e.GetPosition(RowList);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _dragStart = null;
        _selectOnRelease = false;
        if (row.IsEditing || vm.BeginDrag(row) is not { } payload)
        {
            return;
        }
        try
        {
            DragDrop.DoDragDrop(RowList, new DataObject(typeof(TaskDragPayload), payload), DragDropEffects.Move);
        }
        finally
        {
            vm.EndDrag();
            HideDropIndicators();
        }
    }

    /// <summary>複数選択の中の行を押して、運ばずに離した: その1行だけを選ぶ（普通のクリックと同じ）。</summary>
    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectOnRelease && _pressedRow is { } row)
        {
            RowList.SelectRows(new[] { row });
        }
        _dragStart = null;
        _pressedRow = null;
        _selectOnRelease = false;
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        // 自分のタスク以外（他のアプリの文字やファイル）は受けない
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(typeof(TaskDragPayload)) is not TaskDragPayload payload || ViewModel is not { } vm)
        {
            return;
        }
        AutoScroll(e.GetPosition(RowList));
        if (TargetAt(vm, payload, e) is not { } target)
        {
            HideDropIndicators();
            payload.Hint = null;
            return;
        }
        e.Effects = DragDropEffects.Move;
        payload.Hint = target.Plan.Hint;
        ShowDropIndicator(target.Container, target.Row, target.Plan.Position);
    }

    /// <summary>行と行の間を動くたびにも来るので、一覧の外へ出たときだけ消す。</summary>
    private void OnListDragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(RowList);
        if (p.X < 0 || p.Y < 0 || p.X >= RowList.ActualWidth || p.Y >= RowList.ActualHeight)
        {
            HideDropIndicators();
        }
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(TaskDragPayload)) is not TaskDragPayload payload || ViewModel is not { } vm)
        {
            return;
        }
        var target = TargetAt(vm, payload, e);
        HideDropIndicators();
        if (target is { } t)
        {
            await vm.DropAsync(payload, t.Row, t.Plan.Position);
        }
    }

    /// <summary>
    /// マウスの下の行と、行のどこか（上端 1/4＝前、下端 1/4＝後ろ、真ん中＝子）。
    /// 並び替えできない一覧（期限順など）では、行のどこに落としても子にする。落とせなければ null。
    /// </summary>
    private (ListBoxItem Container, TaskRowViewModel Row, DropPlan Plan)? TargetAt(TaskListViewModel vm, TaskDragPayload payload, DragEventArgs e)
    {
        if (ItemAt(e.OriginalSource as DependencyObject) is not { DataContext: TaskRowViewModel row } container)
        {
            return null;
        }
        var y = e.GetPosition(container).Y;
        var height = container.ActualHeight;
        var position = y < height / 4 ? DropPosition.Before : y > height * 3 / 4 ? DropPosition.After : DropPosition.Into;
        var plan = vm.PlanDrop(payload, row, position)
            ?? (position == DropPosition.Into ? null : vm.PlanDrop(payload, row, DropPosition.Into));
        return plan is null ? null : (container, row, plan);
    }

    private static ListBoxItem? ItemAt(DependencyObject? source)
    {
        var node = source;
        while (node is not null and not ListBoxItem)
        {
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return node as ListBoxItem;
    }

    /// <summary>前後に落とすときは行の境目に線（字下げに合わせて左端をそろえる）、子にするときは行を枠で囲む。</summary>
    private void ShowDropIndicator(ListBoxItem container, TaskRowViewModel row, DropPosition position)
    {
        if (position == DropPosition.Into)
        {
            SetDropTarget(row);
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }
        SetDropTarget(null);
        var left = 8 + row.Indent;
        var edge = container.TranslatePoint(new Point(left, position == DropPosition.Before ? 0 : container.ActualHeight), DropLayer);
        Canvas.SetLeft(DropLine, edge.X - 4);
        Canvas.SetTop(DropLine, edge.Y - (DropLine.Height / 2));
        DropLine.Width = Math.Max(0, container.ActualWidth - left);
        DropLine.Visibility = Visibility.Visible;
    }

    private void SetDropTarget(TaskRowViewModel? row)
    {
        if (ReferenceEquals(_dropTargetRow, row))
        {
            return;
        }
        if (_dropTargetRow is { } previous)
        {
            previous.IsDropTarget = false;
        }
        _dropTargetRow = row;
        if (row is not null)
        {
            row.IsDropTarget = true;
        }
    }

    private void HideDropIndicators()
    {
        SetDropTarget(null);
        DropLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>一覧の上下の端（28px）に来たら少しずつスクロールする（DragOver は止まっていても繰り返し来る）。</summary>
    private void AutoScroll(Point p)
    {
        const double Edge = 28;
        const double Step = 14;
        if (FindScrollViewer() is not { } viewer)
        {
            return;
        }
        if (p.Y < Edge)
        {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - Step);
        }
        else if (p.Y > RowList.ActualHeight - Edge)
        {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + Step);
        }
    }
}

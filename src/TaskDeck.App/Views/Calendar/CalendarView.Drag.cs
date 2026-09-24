using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Calendar;

/// <summary>
/// ドラッグで期限・所要時間を変える（F-063、UI 設計書 15.5）。
/// 月表示: チップを別の日へ（時刻は保つ）。週表示: ブロック・チップを時間軸（15分単位）や終日帯へ、ブロックの上下端で所要時間。
/// 押して少し動かすまではボタンのクリック（選ぶ）のまま。動かし始めたらマウスをこの View で捕まえる（ボタンのクリックは起きない）。
/// 仮表示と完了・中止は動かさない。Esc で取りやめ。値の計算と書き込みは CalendarViewModel（CalendarDrag）。
/// </summary>
public partial class CalendarView
{
    private enum DragKind
    {
        Move,
        ResizeTop,
        ResizeBottom,
    }

    private abstract record DropTarget;

    /// <summary>月表示のセル（Cell は MonthGrid の中の位置）。</summary>
    private sealed record DayDrop(DateOnly Date, Rect Cell) : DropTarget;

    /// <summary>週表示の終日帯（Column は AllDayArea の中の位置）。</summary>
    private sealed record AllDayDrop(DateOnly Date, Rect Column) : DropTarget;

    /// <summary>週表示の時間軸（Minute は置き先の開始、所要時間ならカーソルの位置。丸める前の分）。</summary>
    private sealed record TimeDrop(DateOnly Date, double Minute, int ColumnIndex) : DropTarget;

    private sealed class DragState(CalendarEntry entry, UIElement source, Point start, DragKind kind, double grabMinutes)
    {
        public CalendarEntry Entry { get; } = entry;

        public UIElement Source { get; } = source;

        public Point Start { get; } = start;

        public DragKind Kind { get; } = kind;

        /// <summary>時間軸のブロックを掴んだ位置（ブロックの上端から何分下か）。置き先の開始 = カーソルの分 - これ。</summary>
        public double GrabMinutes { get; } = grabMinutes;

        public bool IsDragging { get; set; }
    }

    private DragState? _drag;

    private void InitializeDrag()
    {
        PreviewMouseLeftButtonDown += OnDragMouseDown;
        PreviewMouseMove += OnDragMouseMove;
        PreviewMouseLeftButtonUp += OnDragMouseUp;
        LostMouseCapture += (_, e) =>
        {
            // 子（押したボタン）が捕まえを手放したときも届くので、自分が手放したときだけ取りやめる
            if (ReferenceEquals(e.OriginalSource, this))
            {
                EndDrag();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _drag is { IsDragging: true })
            {
                EndDrag();
                e.Handled = true;
            }
        };
    }

    private void OnDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
        if (MorePopup.IsOpen || e.OriginalSource is not DependencyObject hit)
        {
            return;
        }
        var kind = DragKind.Move;
        for (var node = hit; node is not null && !ReferenceEquals(node, this); node = ParentOf(node))
        {
            if (node is FrameworkElement { Tag: "ResizeTop" })
            {
                kind = DragKind.ResizeTop;
            }
            else if (node is FrameworkElement { Tag: "ResizeBottom" })
            {
                kind = DragKind.ResizeBottom;
            }
            if (node is Button { DataContext: CalendarEntry entry } button)
            {
                if (!entry.CanDrag)
                {
                    return;
                }
                // 時間軸のブロックは掴んだ位置を覚えて、動かしてもブロックがカーソルへ跳ばないようにする
                var grab = entry.HasTime && TimeGrid.IsAncestorOf(button)
                    ? MinuteAt(e.GetPosition(TimeGrid).Y) - entry.StartMinute
                    : 0;
                _drag = new DragState(entry, button, e.GetPosition(this), kind, grab);
                return;
            }
        }
    }

    private void OnDragMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } drag)
        {
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        if (!drag.IsDragging)
        {
            var position = e.GetPosition(this);
            if (Math.Abs(position.X - drag.Start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - drag.Start.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            drag.IsDragging = true;
            drag.Source.Opacity = 0.4; // 掴んだものは元の位置に薄く残す
            CaptureMouse();
        }
        ShowDropFeedback(drag, FindDropTarget(drag, e), e.GetPosition(Root));
        e.Handled = true;
    }

    private async void OnDragMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not { IsDragging: true } drag)
        {
            _drag = null; // 動かしていなければ、ボタンのクリック（選ぶ）に任せる
            return;
        }
        e.Handled = true;
        var target = FindDropTarget(drag, e);
        EndDrag();
        if (target is not null && _viewModel is { } viewModel)
        {
            await DropAsync(viewModel, drag, target);
        }
    }

    private static Task DropAsync(CalendarViewModel viewModel, DragState drag, DropTarget target) => (drag.Kind, target) switch
    {
        (DragKind.Move, DayDrop day) => viewModel.MoveToDateAsync(drag.Entry, day.Date),
        (DragKind.Move, AllDayDrop allDay) => viewModel.MoveToAllDayAsync(drag.Entry, allDay.Date),
        (DragKind.Move, TimeDrop time) => viewModel.MoveToTimeAsync(drag.Entry, time.Date, time.Minute),
        (DragKind.ResizeTop, TimeDrop time) => viewModel.ResizeTopAsync(drag.Entry, time.Minute),
        (DragKind.ResizeBottom, TimeDrop time) => viewModel.ResizeBottomAsync(drag.Entry, time.Minute),
        _ => Task.CompletedTask,
    };

    private void EndDrag()
    {
        var drag = _drag;
        _drag = null;
        drag?.Source.ClearValue(OpacityProperty);
        MonthDropHighlight.Visibility = Visibility.Collapsed;
        AllDayDropHighlight.Visibility = Visibility.Collapsed;
        TimeDropPreview.Visibility = Visibility.Collapsed;
        DropBubble.Visibility = Visibility.Collapsed;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    /// <summary>カーソルの下の置き先。置けない所なら null。</summary>
    private DropTarget? FindDropTarget(DragState drag, MouseEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return null;
        }
        if (viewModel.IsMonth)
        {
            var point = e.GetPosition(MonthGrid);
            if (drag.Kind != DragKind.Move || !Inside(point, MonthGrid) || viewModel.MonthDays.Count != CalendarRange.MonthDays)
            {
                return null;
            }
            var width = MonthGrid.ActualWidth / 7;
            var height = MonthGrid.ActualHeight / 6;
            var column = Math.Min(6, (int)(point.X / width));
            var row = Math.Min(5, (int)(point.Y / height));
            return new DayDrop(viewModel.MonthDays[(row * 7) + column].Date, new Rect(column * width, row * height, width, height));
        }

        if (viewModel.WeekDays.Count != CalendarRange.WeekDays)
        {
            return null;
        }
        var columnWidth = TimeGrid.ActualWidth / 7;
        if (drag.Kind != DragKind.Move)
        {
            // 所要時間: その日の列のまま、縦の位置だけを見る
            var index = IndexOf(viewModel.WeekDays, drag.Entry.Date);
            return index < 0 ? null : new TimeDrop(drag.Entry.Date, MinuteAt(e.GetPosition(TimeGrid).Y), index);
        }
        var allDay = e.GetPosition(AllDayArea);
        if (Inside(allDay, AllDayArea))
        {
            var width = AllDayArea.ActualWidth / 7;
            var column = Math.Min(6, (int)(allDay.X / width));
            return new AllDayDrop(viewModel.WeekDays[column].Date, new Rect(column * width, 0, width, AllDayArea.ActualHeight));
        }
        var viewport = e.GetPosition(TimeScroller);
        var time = e.GetPosition(TimeGrid);
        if (viewport.Y >= 0 && viewport.Y < TimeScroller.ViewportHeight && time.X >= 0 && time.X < TimeGrid.ActualWidth)
        {
            var column = Math.Min(6, (int)(time.X / columnWidth));
            return new TimeDrop(viewModel.WeekDays[column].Date, MinuteAt(time.Y) - drag.GrabMinutes, column);
        }
        return null;
    }

    /// <summary>置き先の枠と、カーソルの横の吹き出し（「9月25日（金） 10:15〜10:45」）。</summary>
    private void ShowDropFeedback(DragState drag, DropTarget? target, Point cursor)
    {
        MonthDropHighlight.Visibility = Visibility.Collapsed;
        AllDayDropHighlight.Visibility = Visibility.Collapsed;
        TimeDropPreview.Visibility = Visibility.Collapsed;
        DropBubble.Visibility = Visibility.Collapsed;
        string text;
        switch (target)
        {
            case DayDrop day:
                Place(MonthDropHighlight, day.Cell);
                text = DisplayText.DateWithWeekday(day.Date);
                break;
            case AllDayDrop allDay:
                Place(AllDayDropHighlight, allDay.Column);
                text = DisplayText.DateWithWeekday(allDay.Date) + " 終日";
                break;
            case TimeDrop time:
                var (start, end) = PreviewSpan(drag, time);
                var width = TimeGrid.ActualWidth / 7;
                var scale = TimeGrid.ActualHeight / CalendarBuilder.MinutesPerDay;
                Place(TimeDropPreview, new Rect((time.ColumnIndex * width) + 2, start * scale, Math.Max(0, width - 4), Math.Max(4, (end - start) * scale)));
                text = DisplayText.DateWithWeekday(time.Date) + " " + TimeLabel(start) + "〜" + TimeLabel(end);
                break;
            default:
                return;
        }
        DropBubbleText.Text = text;
        Canvas.SetLeft(DropBubble, cursor.X + 14);
        Canvas.SetTop(DropBubble, cursor.Y + 14);
        DropBubble.Visibility = Visibility.Visible;
    }

    /// <summary>置いたときの開始・終了（分）。値の丸め方は書き込みと同じ CalendarDrag を通す。</summary>
    private (int Start, int End) PreviewSpan(DragState drag, TimeDrop time)
    {
        var clock = AppServices.Get<IClock>();
        var task = drag.Entry.Task;
        var change = drag.Kind switch
        {
            DragKind.ResizeTop => CalendarDrag.ResizeTop(task, time.Minute, clock),
            DragKind.ResizeBottom => CalendarDrag.ResizeBottom(task, time.Minute, clock),
            _ => CalendarDrag.ToTime(task, time.Date, time.Minute, clock),
        };
        var local = clock.ToLocal(change.DueAt);
        var start = (local.Hour * 60) + local.Minute;
        var duration = change.DurationMinutes is { } m && m > 0 ? m : CalendarEntry.DefaultDurationMinutes;
        return (start, Math.Min(CalendarBuilder.MinutesPerDay, start + duration));
    }

    private double MinuteAt(double y) =>
        TimeGrid.ActualHeight > 0 ? y / TimeGrid.ActualHeight * CalendarBuilder.MinutesPerDay : 0;

    private static void Place(FrameworkElement element, Rect rect)
    {
        Canvas.SetLeft(element, rect.X);
        Canvas.SetTop(element, rect.Y);
        element.Width = rect.Width;
        element.Height = rect.Height;
        element.Visibility = Visibility.Visible;
    }

    private static bool Inside(Point point, FrameworkElement element) =>
        point.X >= 0 && point.Y >= 0 && point.X < element.ActualWidth && point.Y < element.ActualHeight;

    private static int IndexOf(IReadOnlyList<CalendarDay> days, DateOnly date)
    {
        for (var i = 0; i < days.Count; i++)
        {
            if (days[i].Date == date)
            {
                return i;
            }
        }
        return -1;
    }

    private static string TimeLabel(int minute) =>
        string.Create(CultureInfo.InvariantCulture, $"{minute / 60}:{minute % 60:00}");

    /// <summary>見た目の木をたどる（文字の Run など Visual でないものは論理の木で）。</summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}

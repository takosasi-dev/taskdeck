using System.Windows;
using System.Windows.Controls;

namespace TaskDeck.App.Views.Calendar;

/// <summary>
/// 週表示の1日の列。子（ItemsControl の入れ物。DataContext は WeekBlock）を、開始・終了の分と列の番号で置く。
/// 縦は 0:00〜24:00 を自分の高さに割り当て、横は重なりの列の数で割る。
/// </summary>
public sealed class WeekDayPanel : Panel
{
    /// <summary>ブロックどうし・列の端との隙間。</summary>
    private const double Gap = 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        foreach (UIElement child in InternalChildren)
        {
            var rect = Place(child, width, height);
            child.Measure(rect.Size);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(Place(child, finalSize.Width, finalSize.Height));
        }
        return finalSize;
    }

    private static Rect Place(UIElement child, double width, double height)
    {
        if (child is not FrameworkElement { DataContext: WeekBlock block } || block.ColumnCount <= 0)
        {
            return new Rect(0, 0, 0, 0);
        }
        var columnWidth = width / block.ColumnCount;
        var top = height * block.StartMinute / CalendarBuilder.MinutesPerDay;
        var bottom = height * block.EndMinute / CalendarBuilder.MinutesPerDay;
        return new Rect(
            (columnWidth * block.Column) + Gap,
            top + 1,
            Math.Max(0, columnWidth - (Gap * 2)),
            Math.Max(0, bottom - top - 2));
    }
}

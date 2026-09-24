using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskDeck.App.Views.Stats;

/// <summary>
/// 0〜1 の比を Grid の * の幅・高さにする（棒の高さ・割合のバー）。ConverterParameter=rest なら残り（1−値）。
/// </summary>
public sealed class RatioToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ratio = value is double d && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;
        if (parameter is "rest")
        {
            ratio = 1 - ratio;
        }
        return new GridLength(ratio, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"#RRGGBB" をブラシにする（テーマに合わせた値は StatsPresenter が選んである）。読めなければ透明。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        StatsShareBar.BrushFor(value as string) ?? Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool を Visibility に（ConverterParameter=invert で裏返す）。</summary>
public sealed class StatsBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter is "invert")
        {
            visible = !visible;
        }
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// プロジェクト別の内訳の帯（UI 設計書 22.2・モックアップ Stats.dc.html）。高さいっぱいの角丸の帯を、割合の幅で塗り分ける。
/// 区切りは 2px の地（枠線で区切らない）。色はプロジェクト色（テーマに合わせた値）、「プロジェクトなし」「その他」は灰色。
/// </summary>
public sealed class StatsShareBar : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<StatsShare>), typeof(StatsShareBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NoProjectBrushProperty = DependencyProperty.Register(
        nameof(NoProjectBrush), typeof(Brush), typeof(StatsShareBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OtherBrushProperty = DependencyProperty.Register(
        nameof(OtherBrush), typeof(Brush), typeof(StatsShareBar),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Gap = 2;

    public IReadOnlyList<StatsShare>? Items
    {
        get => (IReadOnlyList<StatsShare>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public Brush NoProjectBrush
    {
        get => (Brush)GetValue(NoProjectBrushProperty);
        set => SetValue(NoProjectBrushProperty, value);
    }

    public Brush OtherBrush
    {
        get => (Brush)GetValue(OtherBrushProperty);
        set => SetValue(OtherBrushProperty, value);
    }

    /// <summary>"#RRGGBB" のブラシ（凍結済み）。読めなければ null。</summary>
    public static Brush? BrushFor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var items = Items?.Where(i => i.Share > 0).ToList();
        var width = ActualWidth;
        var height = ActualHeight;
        if (items is null || items.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }
        var radius = height / 2;
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));
        var usable = width - (Gap * (items.Count - 1));
        var total = items.Sum(i => i.Share);
        var x = 0.0;
        foreach (var item in items)
        {
            var w = Math.Max(1, usable * item.Share / total);
            var brush = item.Kind switch
            {
                ShareKind.NoProject => NoProjectBrush,
                ShareKind.Other => OtherBrush,
                _ => BrushFor(item.ColorHex) ?? OtherBrush,
            };
            drawingContext.DrawRectangle(brush, null, new Rect(x, 0, w, height));
            x += w + Gap;
        }
        drawingContext.Pop();
    }
}

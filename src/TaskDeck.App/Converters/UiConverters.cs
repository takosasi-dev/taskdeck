using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskDeck.App.Converters;

/// <summary>
/// アイコンのキー（"IconToday"）を Icons.xaml の StreamGeometry に変える。
/// ViewModel が View の型を持たなくて済むよう、キーだけを持たせるための変換。
/// </summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0)
        {
            return null;
        }
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"#RRGGBB" を Brush に変える（プロジェクト色。テーマ変換は ViewModel 側で済ませてある）。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0)
        {
            return null;
        }
        if (Cache.TryGetValue(hex, out var cached))
        {
            return cached;
        }
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true で隠す（BooleanToVisibilityConverter の逆）。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

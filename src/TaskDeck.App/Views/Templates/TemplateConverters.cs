using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TaskDeck.App.Services;
using TaskDeck.Core.Services;

namespace TaskDeck.App.Views.Templates;

/// <summary>IconKey（Resources/Icons.xaml のキー）→ 図形。見つからなければテンプレートのアイコン。</summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is string key ? Application.Current.TryFindResource(key) as Geometry : null)
        ?? Application.Current.TryFindResource("IconTemplate");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// "#0E7C5A" → ブラシ。ダークテーマではプロジェクト色の対応色（ProjectPalette.ForTheme）。色が無い・読めないときは文字の副色。
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    // ponytail: 開いたときのテーマで色を決める。開いたままテーマを切り替えると、開き直すまで前の色のまま
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var dark = AppServices.IsReady && AppServices.Get<ThemeService>().IsDark;
        if (value is string hex && ThemeService.TryParseColor(ProjectPalette.ForTheme(hex, dark)) is { } color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        return Application.Current.TryFindResource("TextSecondaryBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// 値が「ある」なら Visible（true・空でない文字・null でないもの）。ConverterParameter="Not" で逆にする。
/// </summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            _ => true,
        };
        if (parameter is "Not")
        {
            has = !has;
        }
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

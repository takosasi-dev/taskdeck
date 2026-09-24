using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TaskDeck.App.Services;
using TaskDeck.Core.Services;

namespace TaskDeck.App.Controls.Pickers;

/// <summary>
/// 値が ConverterParameter と同じなら Visible（現在値のチェックマークに使う）。担当: 波1-D。
/// 比較は文字列表現で行う（enum も Guid も同じ書き方で書けるように）。
/// </summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>2つの値が同じなら Visible（一覧の項目と現在値をくらべる）。</summary>
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// プロジェクトの色（#RRGGBB）を、今のテーマに合わせたブラシにする（ProjectPalette.ForTheme）。
/// テーマを切り替えたときは、この変換を使っている一覧を作り直す側で更新する。
/// </summary>
public sealed class ProjectColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Brushes.Transparent;
        }
        var dark = AppServices.IsReady && AppServices.Get<ThemeService>().IsDark;
        var themed = ProjectPalette.ForTheme(hex, dark);
        var color = ThemeService.TryParseColor(themed);
        if (color is null)
        {
            return Brushes.Transparent;
        }
        var brush = new SolidColorBrush(color.Value);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

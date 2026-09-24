using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskDeck.App.Views.Scratch;

/// <summary>true / false でスタイルを選ぶ（全部チェックしたら「捨てる」を主ボタンにする）。</summary>
public sealed class BoolToStyleConverter : IValueConverter
{
    public Style? TrueStyle { get; set; }

    public Style? FalseStyle { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueStyle : FalseStyle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

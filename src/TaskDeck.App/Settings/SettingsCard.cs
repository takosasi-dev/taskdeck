using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskDeck.App.Settings;

/// <summary>
/// 設定カード（UI 設計書 17章）。アイコン20px → タイトル13px＋説明11px → 右にコントロール。
/// 選択肢を見せるもの（配色など）は Expanded にカードの中で展開する。担当: 波1-D。
/// 見た目は Settings/SettingsResources.xaml の既定スタイル。
/// </summary>
public class SettingsCard : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(SettingsCard), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsCard), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(""));

    /// <summary>カードの中に展開する中身（ラジオの並びなど）。null なら展開しない。</summary>
    public static readonly DependencyProperty ExpandedProperty = DependencyProperty.Register(
        nameof(Expanded), typeof(object), typeof(SettingsCard), new PropertyMetadata(null));

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? Expanded
    {
        get => GetValue(ExpandedProperty);
        set => SetValue(ExpandedProperty, value);
    }
}

/// <summary>
/// 設定画面の左カテゴリ（UI 設計書 6.5 と同じ見た目: 選択中は地 Nav.Selected ＋左端に 3×18 のアクセント縦バー）。
/// RadioButton なので、↑↓ での移動と1つだけ選ばれる状態が既定で手に入る。Tag に NavigateTo のページ名を入れる。
/// </summary>
public class SettingsNavItem : System.Windows.Controls.RadioButton
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(SettingsNavItem), new PropertyMetadata(null));

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

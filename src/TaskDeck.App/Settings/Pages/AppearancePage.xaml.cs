using System.Windows.Controls;
using TaskDeck.App.Services;

namespace TaskDeck.App.Settings.Pages;

/// <summary>外観（S-03、UI 設計書 17章）。担当: 波1-D。</summary>
public partial class AppearancePage : UserControl
{
    public AppearancePage(AppearancePageViewModel viewModel, ThemeService theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Windows 側でテーマやアニメーション効果が変わったときも見本と説明を合わせる。
        // 購読は表示中だけ（ページは窓を閉じれば捨てるので、シングルトンに捕まえさせない）
        EventHandler onThemeChanged = (_, _) => viewModel.OnThemeChanged();
        Loaded += (_, _) =>
        {
            theme.ThemeChanged -= onThemeChanged; // Loaded が続けて来ても二重に購読しない
            theme.ReduceMotionChanged -= onThemeChanged;
            theme.ThemeChanged += onThemeChanged;
            theme.ReduceMotionChanged += onThemeChanged;
            viewModel.OnThemeChanged();
        };
        Unloaded += (_, _) =>
        {
            theme.ThemeChanged -= onThemeChanged;
            theme.ReduceMotionChanged -= onThemeChanged;
        };
    }
}

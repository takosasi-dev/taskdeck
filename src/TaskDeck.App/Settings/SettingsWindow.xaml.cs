using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.Settings.Pages;
using Wpf.Ui.Controls;

namespace TaskDeck.App.Settings;

/// <summary>
/// 設定（S-03、UI 設計書 17章）。ShellService.OpenSettings から開く（直接 new しない）。担当: 波1-D。
/// 公開 API（変えない）: NavigateTo(page)。page は PageNames のどれか（null と知らない名前は全般）。
/// ページはカテゴリごとの UserControl（Settings/Pages/*Page.xaml）で、開いたときに DI から作って使い回す。
/// Esc で閉じる（開いているドロップダウンがあれば、先にそちらが閉じる）。
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    /// <summary>NavigateTo に渡せるページ名（左のカテゴリの並び順）。</summary>
    public static IReadOnlyList<string> PageNames { get; } =
        ["general", "appearance", "notifications", "shortcuts", "external", "data", "about"];

    private readonly IServiceProvider _services;
    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.OrdinalIgnoreCase);

    public SettingsWindow(IServiceProvider services, AppPaths paths)
    {
        _services = services;
        InitializeComponent();
        SettingsPathText.Text = paths.SettingsPath;
        // KeyDown（バブル）で受ける: ComboBox が Esc でドロップダウンを閉じたとき（Handled）は窓を閉じない
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        NavigateTo(null);
    }

    public void NavigateTo(string? page)
    {
        var key = page?.Trim() is { Length: > 0 } name && PageNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? name
            : "general";
        var item = NavItems().First(i => string.Equals((string)i.Tag, key, StringComparison.OrdinalIgnoreCase));
        item.IsChecked = true;
        ShowPage(key);
    }

    private IEnumerable<SettingsNavItem> NavItems() =>
        NavTop.Children.OfType<SettingsNavItem>().Concat(NavBottom.Children.OfType<SettingsNavItem>());

    private void OnNavChecked(object sender, RoutedEventArgs e) => ShowPage((string)((SettingsNavItem)sender).Tag);

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
        }
        if (!ReferenceEquals(PageHost.Content, page))
        {
            PageHost.Content = page;
            PageScroller.ScrollToTop();
        }
    }

    private UserControl CreatePage(string key) => key.ToLowerInvariant() switch
    {
        "appearance" => _services.GetRequiredService<AppearancePage>(),
        "notifications" => _services.GetRequiredService<NotificationsPage>(),
        "shortcuts" => _services.GetRequiredService<ShortcutsPage>(),
        "external" => _services.GetRequiredService<ExternalPage>(),
        "data" => _services.GetRequiredService<DataPage>(),
        "about" => _services.GetRequiredService<AboutPage>(),
        _ => _services.GetRequiredService<GeneralPage>(),
    };
}

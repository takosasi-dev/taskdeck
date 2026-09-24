using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.Settings;
using TaskDeck.App.Settings.Pages;

namespace TaskDeck.App.Registration;

// 担当: 波1-D（テーマ・ピッカー・設定画面）
internal static partial class ServiceRegistration
{
    static partial void AddAppearance(IServiceCollection services)
    {
        services.AddTransient<SettingsWindow>();

        // 設定のページ（カテゴリごと。開いたときに作って、ウィンドウが閉じるまで使い回す）
        services.AddTransient<GeneralPage>();
        services.AddTransient<AppearancePage>();
        services.AddTransient<NotificationsPage>();
        services.AddTransient<ShortcutsPage>();
        services.AddTransient<ExternalPage>();
        services.AddTransient<DataPage>();
        services.AddTransient<AboutPage>();

        services.AddTransient<GeneralPageViewModel>();
        services.AddTransient<AppearancePageViewModel>();
        services.AddTransient<NotificationsPageViewModel>();
        services.AddTransient<ShortcutsPageViewModel>();
        services.AddTransient<ExternalPageViewModel>();
        services.AddTransient<DataPageViewModel>();
        services.AddTransient<AboutPageViewModel>();

        // 開発用のデータフォルダのときだけ働く入口（本番では何もしない）
        services.AddHostedService<DevSettingsLauncher>();
    }
}

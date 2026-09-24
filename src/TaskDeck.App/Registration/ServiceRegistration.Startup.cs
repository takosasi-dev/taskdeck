using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.Views.Startup;

namespace TaskDeck.App.Registration;

// 担当: 波4-L（初回起動。起動画面は DI を組む前に App.xaml.cs が作るので、ここには置かない）
internal static partial class ServiceRegistration
{
    static partial void AddStartup(IServiceCollection services)
    {
        services.AddSingleton<FirstRunService>();
        services.AddTransient<FirstRunViewModel>();
        services.AddTransient<FirstRunWindow>();
        if (SplashDevLauncher.IsRequested)
        {
            // 起動画面の確認用の入口（開発用データフォルダのときだけ働く。本番では登録もしない）
            services.AddHostedService<SplashDevLauncher>();
        }
    }
}

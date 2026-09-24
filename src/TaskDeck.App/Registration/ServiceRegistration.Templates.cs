using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.Views.Templates;

namespace TaskDeck.App.Registration;

// 担当: 波2-F（繰り返し設定・テンプレート画面）
internal static partial class ServiceRegistration
{
    static partial void AddTemplates(IServiceCollection services)
    {
        services.AddTransient<TemplatesViewModel>();
        services.AddTransient<TemplatesWindow>();
        if (TemplatesDevTools.IsRequested)
        {
            // 確認用の入口（開発用データフォルダのときだけ起動後にテンプレート画面を開く。本番では登録もしない）
            services.AddHostedService<TemplatesDevLauncher>();
        }
    }
}

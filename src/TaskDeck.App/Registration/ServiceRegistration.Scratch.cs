using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.Views.Scratch;
using TaskDeck.Core.Scratch;

namespace TaskDeck.App.Registration;

// 担当: 波3-N（使い捨てリスト）
internal static partial class ServiceRegistration
{
    static partial void AddScratch(IServiceCollection services)
    {
        services.AddSingleton<ScratchStore>();
        services.AddSingleton<ScratchPresenter>();
        services.AddSingleton<ScratchPaneViewModel>();
        services.AddTransient<ScratchWindowViewModel>();
        services.AddTransient<ScratchWindow>();
        // 起動時に読み、アプリを閉じるときに保存し切る
        services.AddHostedService<ScratchLifetime>();
        if (ScratchDevLauncher.IsRequested)
        {
            // 確認用の入口（開発用データフォルダのときだけ動く。本番では登録もしない）
            services.AddHostedService<ScratchDevLauncher>();
        }
    }
}

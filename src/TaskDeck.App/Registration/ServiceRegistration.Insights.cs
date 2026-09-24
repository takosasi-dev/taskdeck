using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskDeck.App.BackgroundTasks;
using TaskDeck.App.Views.Stats;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;
using TaskDeck.Data.Export;
using TaskDeck.Data.External;

namespace TaskDeck.App.Registration;

// 担当: 波3-K（振り返り・Markdown・外部API）
internal static partial class ServiceRegistration
{
    static partial void AddInsights(IServiceCollection services)
    {
        // 外部API（すべて任意機能。通信の決まりは ExternalHttp にまとめてある）。応答は 1MB まで（それ以上は失敗として捨てる）
        var http = services.AddHttpClient(ExternalHttp.ClientName, client => client.MaxResponseContentBufferSize = 1 << 20);
        DevFakeExternalHandler.AttachIfRequested(http);
        services.AddSingleton<ExternalHttp>();
        services.AddSingleton<HolidayUpdater>();
        services.AddSingleton<WeatherUpdater>();
        services.AddSingleton<PlaceSearch>();
        services.AddSingleton<LinkPreviewService>();
        services.AddSingleton(sp => new UpdateChecker(
            sp.GetRequiredService<ExternalHttp>(),
            sp.GetRequiredService<IAppStateRepository>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<UpdateChecker>>(),
            typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0),
            DevFakeExternalHandler.Repository ?? UpdateChecker.Repository));
        services.AddHostedService<ExternalDataWorker>();

        // 振り返り（メイン画面の中の1枚。アプリと同じ寿命）と Markdown 出力（F-108・F-109）
        services.AddSingleton<MarkdownExporter>();
        services.AddSingleton<StatsExportViewModel>();
        services.AddSingleton<StatsViewModel>();
    }
}

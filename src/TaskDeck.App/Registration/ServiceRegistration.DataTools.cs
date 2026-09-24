using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Views.DataTools;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Data;
using TaskDeck.Data.Backup;
using TaskDeck.Data.ImportExport;

namespace TaskDeck.App.Registration;

// 担当: 波2-G（入出力・複合絞り込み）
internal static partial class ServiceRegistration
{
    static partial void AddDataTools(IServiceCollection services)
    {
        services.AddSingleton<JsonExporter>();
        services.AddSingleton(sp => new JsonImporter(
            sp.GetRequiredService<IDbContextFactory<TaskDeckDbContext>>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<DataChangeHub>(),
            sp.GetRequiredService<ILogger<JsonImporter>>(),
            // 置換の前に1世代とる（起動時バックアップと同じ backups フォルダの5世代の中で回る）
            () => sp.GetRequiredService<BackupService>().CreateStartupBackup()));
        services.AddSingleton<CsvExporter>();
        services.AddTransient<ImportExportViewModel>();

        // 開発用の確認窓（開発用データフォルダで TASKDECK_DEV_DATATOOLS があるときだけ開く。本番では何もしない）
        services.AddTransient<DataToolsDevWindow>();
        services.AddHostedService<DataToolsDevStartup>();
    }
}

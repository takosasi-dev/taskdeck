using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskDeck.Core.Abstractions;
using TaskDeck.Data.Backup;
using TaskDeck.Data.External;
using TaskDeck.Data.Infrastructure;
using TaskDeck.Data.Maintenance;
using TaskDeck.Data.Repositories;
using TaskDeck.Data.Seeding;

namespace TaskDeck.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Data 層の登録。IClock・DataChangeHub・ISettingsStore・IRecurrenceEngine は App 側で登録する。</summary>
    public static IServiceCollection AddTaskDeckData(this IServiceCollection services, TaskDeckDataOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<AuditInterceptor>();
        services.AddDbContextFactory<TaskDeckDbContext>((sp, o) => o
            .UseSqlite(options.ConnectionString)
            .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));

        services.AddSingleton<BackupService>();
        services.AddSingleton<DatabaseInitializer>();

        services.AddSingleton<ITaskRepository, TaskRepository>();
        services.AddSingleton<IProjectRepository, ProjectRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<ITemplateRepository, TemplateRepository>();
        services.AddSingleton<IAppStateRepository, AppStateRepository>();

        services.AddSingleton<HolidayCache>();
        services.AddSingleton<IHolidayProvider>(sp => sp.GetRequiredService<HolidayCache>());
        services.AddSingleton<WeatherCache>();
        services.AddSingleton<IWeatherProvider>(sp => sp.GetRequiredService<WeatherCache>());

        // 起動時に App から呼ぶもの（呼び出しは統合担当が足す）
        services.AddSingleton<DatabaseMaintenance>();
        services.AddSingleton<SampleDataSeeder>();
        services.AddSingleton<DevDataSeeder>();
        return services;
    }
}

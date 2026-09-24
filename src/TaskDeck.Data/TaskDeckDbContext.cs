using Microsoft.EntityFrameworkCore;
using TaskDeck.Core.Entities;
using TaskDeck.Data.Infrastructure;

namespace TaskDeck.Data;

/// <summary>
/// TaskDeck の DB。IDbContextFactory で操作ごとに作って捨てる（使い回さない）。
/// 設定は Configurations/ の Fluent API にだけ書く（Core のエンティティに属性を付けない）。
/// </summary>
public sealed class TaskDeckDbContext(DbContextOptions<TaskDeckDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TaskTag> TaskTags => Set<TaskTag>();
    public DbSet<RecurrenceRule> RecurrenceRules => Set<RecurrenceRule>();
    public DbSet<TaskTemplate> Templates => Set<TaskTemplate>();
    public DbSet<TaskTemplateItem> TemplateItems => Set<TaskTemplateItem>();
    public DbSet<SyncMeta> SyncMeta => Set<SyncMeta>();
    public DbSet<AppStateEntry> AppState => Set<AppStateEntry>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<WeatherDay> WeatherDays => Set<WeatherDay>();
    public DbSet<LinkPreview> LinkPreviews => Set<LinkPreview>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TaskDeckDbContext).Assembly);
}

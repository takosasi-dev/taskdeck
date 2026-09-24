using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskDeck.Core.Entities;
using TaskDeck.Data.Infrastructure;

namespace TaskDeck.Data.Configurations;

// テーブル定義（設計書 3.3〜3.8、4.7.9）。表名は設計書の名前に合わせる。
// 外部キーは張るが、物理削除はゴミ箱の掃除とテンプレート項目くらいなので、ほとんどは SetNull にしておく。

internal sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> b)
    {
        b.ToTable("TaskItem", t => t.HasCheckConstraint("CK_TaskItem_Depth", "\"Depth\" >= 0 AND \"Depth\" <= 2"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(TaskItem.TitleMaxLength);
        b.Property<string>(AuditInterceptor.SearchKeyProperty).IsRequired().HasDefaultValue("");
        b.Ignore(x => x.IsOpen);
        b.Ignore(x => x.IsDeleted);

        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.ParentTaskId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<RecurrenceRule>().WithMany().HasForeignKey(x => x.RecurrenceRuleId).OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => new { x.DeletedAt, x.Status, x.DueAt }).HasDatabaseName("IX_Task_Due");
        b.HasIndex(x => new { x.ProjectId, x.DeletedAt }).HasDatabaseName("IX_Task_Project");
        b.HasIndex(x => x.ParentTaskId).HasDatabaseName("IX_Task_Parent");
        b.HasIndex(x => x.SyncState).HasDatabaseName("IX_Task_Sync");
        b.HasIndex(x => x.RecurrenceSeriesId).HasDatabaseName("IX_Task_Series");
        b.HasIndex(x => new { x.Status, x.CompletedAt }).HasDatabaseName("IX_Task_Completed");
        b.HasIndex(x => x.TemplateBatchId).HasDatabaseName("IX_Task_Batch");
        b.HasIndex(x => x.RemindAt).HasDatabaseName("IX_Task_Remind")
            .HasFilter("\"NotifiedAt\" IS NULL AND \"DeletedAt\" IS NULL");
    }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("Project");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(Project.NameMaxLength);
        b.Property(x => x.ColorHex).IsRequired().HasMaxLength(7);
        b.Property(x => x.IconKey).HasMaxLength(50);
        b.HasIndex(x => new { x.DeletedAt, x.SortOrder }).HasDatabaseName("IX_Project_Order");
    }
}

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> b)
    {
        b.ToTable("Tag");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(Tag.NameMaxLength).UseCollation("NOCASE");
        b.Property(x => x.ColorHex).IsRequired().HasMaxLength(7);
        // 大文字小文字を無視して一意。削除済みのタグは除く（同じ名前で作り直せるように）
        b.HasIndex(x => x.Name).IsUnique().HasFilter("\"DeletedAt\" IS NULL").HasDatabaseName("UX_Tag_Name");
    }
}

internal sealed class TaskTagConfiguration : IEntityTypeConfiguration<TaskTag>
{
    public void Configure(EntityTypeBuilder<TaskTag> b)
    {
        b.ToTable("TaskTag");
        b.HasKey(x => new { x.TaskId, x.TagId });
        b.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.TagId, x.DeletedAt }).HasDatabaseName("IX_TaskTag_Tag");
    }
}

internal sealed class RecurrenceRuleConfiguration : IEntityTypeConfiguration<RecurrenceRule>
{
    public void Configure(EntityTypeBuilder<RecurrenceRule> b)
    {
        b.ToTable("RecurrenceRule");
        b.HasKey(x => x.Id);
        b.Property(x => x.RRule).IsRequired().HasMaxLength(RecurrenceRule.RRuleMaxLength);
    }
}

internal sealed class TaskTemplateConfiguration : IEntityTypeConfiguration<TaskTemplate>
{
    public void Configure(EntityTypeBuilder<TaskTemplate> b)
    {
        b.ToTable("TaskTemplate");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired().HasMaxLength(TaskTemplate.NameMaxLength);
        b.Property(x => x.Description).HasMaxLength(TaskTemplate.DescriptionMaxLength);
        b.Property(x => x.IconKey).HasMaxLength(50);
        b.Property(x => x.ColorHex).HasMaxLength(7);
        b.Property(x => x.AnchorLabel).HasMaxLength(TaskTemplate.AnchorLabelMaxLength);
        b.Property(x => x.DefaultTagIds)
            .HasConversion(new GuidListConverter(), new GuidListComparer())
            .IsRequired();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.DefaultProjectId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.DeletedAt, x.UseCount }).IsDescending(false, true).HasDatabaseName("IX_Template_Use");
    }
}

internal sealed class TaskTemplateItemConfiguration : IEntityTypeConfiguration<TaskTemplateItem>
{
    public void Configure(EntityTypeBuilder<TaskTemplateItem> b)
    {
        b.ToTable("TaskTemplateItem", t => t.HasCheckConstraint("CK_TaskTemplateItem_Depth", "\"Depth\" >= 0 AND \"Depth\" <= 2"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(TaskItem.TitleMaxLength);
        b.Property(x => x.DueTime).HasConversion(new HourMinuteConverter()).HasMaxLength(5);
        b.Property(x => x.TagIds)
            .HasConversion(new GuidListConverter(), new GuidListComparer())
            .IsRequired();
        b.HasOne<TaskTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<TaskTemplateItem>().WithMany().HasForeignKey(x => x.ParentItemId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.TemplateId, x.SortOrder }).HasDatabaseName("IX_TemplateItem_Tpl");
    }
}

internal sealed class SyncMetaConfiguration : IEntityTypeConfiguration<SyncMeta>
{
    public void Configure(EntityTypeBuilder<SyncMeta> b)
    {
        b.ToTable("SyncMeta");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.LastSyncCursor).HasMaxLength(100);
        b.Property(x => x.UserId).HasMaxLength(100);
    }
}

internal sealed class AppStateEntryConfiguration : IEntityTypeConfiguration<AppStateEntry>
{
    public void Configure(EntityTypeBuilder<AppStateEntry> b)
    {
        b.ToTable("AppState");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(100);
    }
}

internal sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> b)
    {
        b.ToTable("Holiday");
        b.HasKey(x => x.Date);
        b.Property(x => x.LocalName).IsRequired().HasMaxLength(100);
        b.Property(x => x.CountryCode).IsRequired().HasMaxLength(2);
    }
}

internal sealed class WeatherDayConfiguration : IEntityTypeConfiguration<WeatherDay>
{
    public void Configure(EntityTypeBuilder<WeatherDay> b)
    {
        b.ToTable("WeatherCache");
        b.HasKey(x => x.Date);
    }
}

internal sealed class LinkPreviewConfiguration : IEntityTypeConfiguration<LinkPreview>
{
    public void Configure(EntityTypeBuilder<LinkPreview> b)
    {
        b.ToTable("LinkPreview");
        b.HasKey(x => x.Url);
        b.Property(x => x.Url).HasMaxLength(2048);
        b.Property(x => x.Title).HasMaxLength(500);
        b.Property(x => x.Description).HasMaxLength(1000);
    }
}

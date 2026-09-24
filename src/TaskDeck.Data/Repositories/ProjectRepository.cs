using Microsoft.EntityFrameworkCore;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Repositories;

public sealed class ProjectRepository(IDbContextFactory<TaskDeckDbContext> factory, IClock clock, DataChangeHub hub) : IProjectRepository
{
    public async Task<IReadOnlyList<Project>> GetAllAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking()
            .Where(p => p.DeletedAt == null && (includeArchived || !p.IsArchived))
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<Project?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
    }

    public async Task<Project> AddAsync(string name, string? colorHex = null, CancellationToken ct = default)
    {
        var normalized = NormalizeName(name);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("プロジェクト名が空です。", nameof(name));
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var project = await NewProjectAsync(db, normalized, colorHex, ct);
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Projects);
        return project;
    }

    public async Task<Project> GetOrCreateAsync(string name, CancellationToken ct = default)
    {
        var normalized = NormalizeName(name);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("プロジェクト名が空です。", nameof(name));
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var lower = normalized.ToLowerInvariant();
        var existing = await db.Projects.AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == lower)
            .OrderBy(p => p.IsArchived).ThenBy(p => p.SortOrder)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }
        var project = await NewProjectAsync(db, normalized, null, ct);
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Projects);
        return project;
    }

    public async Task UpdateAsync(Guid id, Action<Project> mutate, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
        if (project is null)
        {
            return;
        }
        var before = project.Clone();
        mutate(project);
        if (project.Id != before.Id || !project.SortOrder.Equals(before.SortOrder) || project.DeletedAt != before.DeletedAt || project.CreatedAt != before.CreatedAt)
        {
            throw new InvalidOperationException("UpdateAsync で変えられるのは Name, ColorHex, IconKey, IsArchived だけです。");
        }
        var name = NormalizeName(project.Name);
        project.Name = name.Length == 0 ? before.Name : name;
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Projects);
    }

    public async Task ReorderAsync(Guid id, double sortOrder, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
        if (project is null)
        {
            return;
        }
        project.SortOrder = sortOrder;
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Projects);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
        if (project is null)
        {
            return;
        }
        // 所属タスクは消さずに「プロジェクトなし」へ移す（F-042）。削除済みのタスクも含めて外す
        var tasks = await db.Tasks.Where(t => t.ProjectId == id).ToListAsync(ct);
        foreach (var t in tasks)
        {
            t.ProjectId = null;
        }
        var templates = await db.Templates.Where(t => t.DefaultProjectId == id).ToListAsync(ct);
        foreach (var t in templates)
        {
            t.DefaultProjectId = null;
        }
        project.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Projects | DataChangeKind.Tasks | DataChangeKind.Templates, [.. tasks.Select(t => t.Id)]);
    }

    internal static string NormalizeName(string? name) =>
        TextNormalizer.TruncateGraphemes(TextNormalizer.ForName(name), Project.NameMaxLength);

    private static async Task<Project> NewProjectAsync(TaskDeckDbContext db, string name, string? colorHex, CancellationToken ct)
    {
        var used = await db.Projects.Where(p => p.DeletedAt == null).Select(p => p.ColorHex).ToListAsync(ct);
        var maxOrder = await db.Projects.Where(p => p.DeletedAt == null).MaxAsync(p => (double?)p.SortOrder, ct);
        return new Project
        {
            Name = name,
            ColorHex = colorHex ?? ProjectPalette.NextColor(used),
            SortOrder = SortOrderMath.Between(maxOrder, null),
        };
    }
}

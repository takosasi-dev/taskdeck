using Microsoft.EntityFrameworkCore;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Repositories;

public sealed class TagRepository(IDbContextFactory<TaskDeckDbContext> factory, IClock clock, DataChangeHub hub) : ITagRepository
{
    public async Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tags.AsNoTracking().Where(t => t.DeletedAt == null).OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<Tag> GetOrCreateAsync(string name, CancellationToken ct = default)
    {
        var normalized = NormalizeName(name);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("タグ名が空です。", nameof(name));
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Tags.AsNoTracking().FirstOrDefaultAsync(t => t.DeletedAt == null && t.Name == normalized, ct);
        if (existing is not null)
        {
            return existing;
        }
        var tag = new Tag { Name = normalized };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Tags);
        return tag;
    }

    public async Task<OperationResult> RenameAsync(Guid id, string newName, CancellationToken ct = default)
    {
        var normalized = NormalizeName(newName);
        if (normalized.Length == 0)
        {
            return OperationResult.Fail("タグ名を入力してください。");
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
        if (tag is null)
        {
            return OperationResult.Fail("タグが見つかりません。");
        }
        var duplicate = await db.Tags.AnyAsync(t => t.Id != id && t.DeletedAt == null && t.Name == normalized, ct);
        if (duplicate)
        {
            return OperationResult.Fail($"「{normalized}」というタグは既にあります。");
        }
        tag.Name = normalized;
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Tags);
        return OperationResult.Success();
    }

    public async Task UpdateColorAsync(Guid id, string colorHex, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
        if (tag is null)
        {
            return;
        }
        tag.ColorHex = colorHex;
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Tags);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
        if (tag is null)
        {
            return;
        }
        var now = clock.UtcNow;
        var links = await db.TaskTags.Where(tt => tt.TagId == id && tt.DeletedAt == null).ToListAsync(ct);
        foreach (var link in links)
        {
            link.DeletedAt = now;
        }
        tag.DeletedAt = now;
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Tags | DataChangeKind.Tasks, [.. links.Select(l => l.TaskId)]);
    }

    public async Task<int> DeleteUnusedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var unused = await db.Tags
            .Where(tag => tag.DeletedAt == null
                && !db.TaskTags.Any(tt => tt.TagId == tag.Id && tt.DeletedAt == null
                    && db.Tasks.Any(t => t.Id == tt.TaskId && t.DeletedAt == null)))
            .ToListAsync(ct);
        if (unused.Count == 0)
        {
            return 0;
        }
        var now = clock.UtcNow;
        foreach (var tag in unused)
        {
            tag.DeletedAt = now;
        }
        await db.SaveChangesAsync(ct);
        hub.Publish(DataChangeKind.Tags);
        return unused.Count;
    }

    /// <summary>タグ名の正規化: NFKC・前後空白除去・先頭の # を除く・空白は使えないので _ に・50文字まで。</summary>
    public static string NormalizeName(string? name)
    {
        var s = TextNormalizer.ForName(name).TrimStart('#').Trim().Replace(' ', '_');
        return TextNormalizer.TruncateGraphemes(s, Tag.NameMaxLength);
    }
}

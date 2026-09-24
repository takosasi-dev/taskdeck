using Microsoft.EntityFrameworkCore;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;

namespace TaskDeck.Data.Repositories;

public sealed class AppStateRepository(IDbContextFactory<TaskDeckDbContext> factory) : IAppStateRepository
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AppState.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entry = await db.AppState.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (value is null)
        {
            if (entry is not null)
            {
                db.AppState.Remove(entry);
                await db.SaveChangesAsync(ct);
            }
            return;
        }
        if (entry is null)
        {
            db.AppState.Add(new AppStateEntry { Key = key, Value = value });
        }
        else
        {
            entry.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }
}

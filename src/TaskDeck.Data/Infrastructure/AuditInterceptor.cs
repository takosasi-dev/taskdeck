using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Infrastructure;

/// <summary>
/// 保存の直前に、監査列と検索キーを一括で埋める（書き忘れが起きないようにここだけで行う）。
/// 追加: CreatedAt（未設定なら）、UpdatedAt、SyncState=Pending。更新: UpdatedAt、SyncState=Pending。
/// タスクはシャドウプロパティ SearchKey（タイトル＋メモを NFKC・小文字化したもの）も更新する。
/// </summary>
public sealed class AuditInterceptor(IClock clock) : SaveChangesInterceptor
{
    public const string SearchKeyProperty = "SearchKey";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }
        var now = clock.UtcNow;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }
            switch (entry.Entity)
            {
                case ISyncTracked tracked:
                    if (entry.State == EntityState.Added && tracked.CreatedAt == default)
                    {
                        tracked.CreatedAt = now;
                    }
                    tracked.UpdatedAt = now;
                    tracked.SyncState = SyncState.Pending;
                    break;
                case AppStateEntry state:
                    state.UpdatedAt = now;
                    break;
            }
            if (entry.Entity is TaskItem task)
            {
                entry.Property(SearchKeyProperty).CurrentValue = TextNormalizer.SearchKey(task.Title, task.Notes);
            }
        }
    }
}

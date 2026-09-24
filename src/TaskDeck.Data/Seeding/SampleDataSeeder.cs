using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Seeding;

/// <summary>
/// 初回だけ入れるサンプル（F-184）。空の画面を見せないためのもので、機能を1つずつ示す
/// （期限あり／サブタスクあり／タグあり）。既にタスクがあるときは入れず、印だけ残す。
/// </summary>
public sealed class SampleDataSeeder(
    IDbContextFactory<TaskDeckDbContext> factory,
    ITaskRepository tasks,
    IAppStateRepository state,
    IClock clock,
    ILogger<SampleDataSeeder> logger)
{
    /// <summary>入れたら true。2回目以降は何もしない。</summary>
    public async Task<bool> SeedIfNeededAsync(CancellationToken ct = default)
    {
        if (await state.GetAsync(AppStateKeys.SampleDataSeeded, ct) is not null)
        {
            return false;
        }
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            if (await db.Tasks.AnyAsync(ct))
            {
                await state.SetAsync(AppStateKeys.SampleDataSeeded, "skipped", ct);
                return false;
            }
        }

        var parentId = Guid.CreateVersion7();
        await tasks.AddManyAsync(
            [
                new NewTaskRequest
                {
                    Title = "今日やることを1つ決める",
                    Notes = "期限を付けたタスクは「今日」に出ます。期限は右の詳細ペインから変えられます。",
                    DueAt = TaskRules.DateOnlyDue(clock.LocalToday(), clock),
                },
                new NewTaskRequest
                {
                    Id = parentId,
                    Title = "机まわりを片づける",
                    Notes = "サブタスクは Tab キーで1段下げて作れます（3階層まで）。",
                },
                new NewTaskRequest { Title = "書類を分けて捨てる", ParentTaskId = parentId },
                new NewTaskRequest { Title = "ケーブルをまとめる", ParentTaskId = parentId },
                new NewTaskRequest
                {
                    Title = "タグで分類してみる",
                    Notes = "タイトルに #タグ名 と書くとタグが付きます。サイドバーのタグから絞り込めます。",
                    TagNames = ["サンプル"],
                },
            ],
            ct);
        await state.SetAsync(AppStateKeys.SampleDataSeeded, "1", ct);
        logger.LogInformation("サンプルタスクを入れました");
        return true;
    }
}

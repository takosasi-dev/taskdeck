using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Seeding;

/// <summary>
/// 性能計測用のデータ投入（開発時だけ使う。製品の画面からは呼ばない）。
/// プロジェクト10・タグ20に、未完了 300 件前後と、過去1年に散らばった完了タスクを作る。
/// 1万件を数秒で入れるため、1つの DbContext にためて1回で保存する。randomSeed が同じなら毎回同じデータになる。
/// </summary>
public sealed class DevDataSeeder(
    IDbContextFactory<TaskDeckDbContext> factory,
    IClock clock,
    DataChangeHub hub,
    ILogger<DevDataSeeder> logger)
{
    private static readonly string[] ProjectNames =
        ["業務改善", "資料作成", "採用", "経理", "営業", "学習", "家事", "健康", "買い物", "趣味"];

    private static readonly string[] TagNames =
        ["仕事", "会議", "電話", "メール", "急ぎ", "待ち", "家", "外出", "読書", "調査",
         "レビュー", "企画", "経費", "連絡", "買い物", "運動", "勉強", "英語", "手続き", "趣味"];

    private static readonly string[] Subjects =
        ["会議資料", "定例会議", "会議室", "見積書", "議事録", "請求書", "企画書", "週報", "提案書", "契約書",
         "旅費精算", "面談の記録", "手順書", "検証環境", "問い合わせ", "部屋の掃除", "洗濯", "家計簿", "healthcheck ログ", "AWS の設定"];

    private static readonly string[] Verbs =
        ["をまとめる", "を確認する", "を送る", "を作成する", "を直す", "を印刷する", "を提出する", "を読む", "を予約する", "を片づける"];

    private static readonly string[] NoteLines =
        ["前回の内容を踏まえて進める。", "関係者に共有してから進める。", "時間がかかりそうなら分割する。", "先に見積もりを取る。"];

    /// <summary>count 件のタスクを入れて、入れた件数を返す。</summary>
    public async Task<int> SeedAsync(int count, int randomSeed = 20260923, CancellationToken ct = default)
    {
        if (count <= 0)
        {
            return 0;
        }
        var watch = Stopwatch.StartNew();
        var rng = new Random(randomSeed);
        var now = clock.UtcNow;
        var today = clock.LocalToday();

        await using var db = await factory.CreateDbContextAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var projects = ProjectNames
            .Select((name, i) => new Project
            {
                Name = name,
                ColorHex = ProjectPalette.Light[i % ProjectPalette.Light.Count],
                SortOrder = (i + 1) * SortOrderMath.Step,
            })
            .ToList();
        var tags = TagNames.Select(name => new Tag { Name = name }).ToList();

        var tasks = new List<TaskItem>(count);
        var links = new List<TaskTag>(count);
        var rules = new List<RecurrenceRule>();
        var openTarget = Math.Min(300, Math.Max(1, count / 3));
        var order = 0d;

        while (tasks.Count < count)
        {
            var open = tasks.Count < openTarget;
            var task = new TaskItem
            {
                Title = Pick(rng, Subjects) + Pick(rng, Verbs),
                Notes = rng.NextDouble() < 0.2 ? Pick(rng, NoteLines) : null,
                Priority = (Priority)rng.Next(0, 5),
                ProjectId = rng.NextDouble() < 0.7 ? Pick(rng, projects).Id : null,
                SortOrder = order += SortOrderMath.Step,
            };

            if (open)
            {
                task.Status = rng.NextDouble() < 0.25 ? TaskItemStatus.InProgress : TaskItemStatus.NotStarted;
                task.CreatedAt = now.AddDays(-rng.Next(0, 60)).AddMinutes(-rng.Next(0, 1440));
                if (rng.NextDouble() < 0.7)
                {
                    var due = today.AddDays(rng.Next(-10, 30));
                    var hasTime = rng.NextDouble() < 0.3;
                    task.DueHasTime = hasTime;
                    task.DueAt = hasTime
                        ? clock.LocalToUtc(due, new TimeOnly(rng.Next(9, 20), rng.Next(0, 4) * 15))
                        : clock.LocalDayStartUtc(due);
                    if (rng.NextDouble() < 0.15)
                    {
                        task.RemindOffsetMinutes = Pick(rng, [0, 15, 60, 1440]);
                        task.RemindAt = TaskRules.ComputeRemindAt(task.DueAt, task.DueHasTime, task.RemindOffsetMinutes, new TimeOnly(9, 0), clock);
                    }
                }
                if (rules.Count < 12 && rng.NextDouble() < 0.06)
                {
                    var rule = new RecurrenceRule
                    {
                        RRule = rng.NextDouble() < 0.5 ? RecurrencePresets.Daily() : RecurrencePresets.Weekdays(),
                        AnchorAt = task.DueAt ?? now,
                    };
                    rules.Add(rule);
                    task.RecurrenceRuleId = rule.Id;
                    task.RecurrenceSeriesId = task.Id;
                }
            }
            else
            {
                var closed = now.AddDays(-rng.Next(0, 365)).AddMinutes(-rng.Next(0, 1440));
                task.Status = rng.NextDouble() < 0.93 ? TaskItemStatus.Completed : TaskItemStatus.Cancelled;
                task.CompletedAt = closed;
                task.CreatedAt = closed.AddDays(-rng.Next(0, 30));
                if (rng.NextDouble() < 0.6)
                {
                    task.DueAt = clock.LocalDayStartUtc(clock.ToLocalDate(closed).AddDays(rng.Next(-3, 3)));
                }
                if (rng.NextDouble() < 0.005)
                {
                    task.DeletedAt = now.AddDays(-rng.Next(0, 60));   // ゴミ箱（掃除の確認用に古いものも混ぜる）
                }
            }
            tasks.Add(task);
            AddTags(rng, links, tags, task.Id);

            // 5% くらいは 1〜3 件のサブタスクを付ける
            if (rng.NextDouble() < 0.05)
            {
                var children = rng.Next(1, 4);
                for (var i = 0; i < children && tasks.Count < count; i++)
                {
                    var child = new TaskItem
                    {
                        Title = Pick(rng, Subjects) + Pick(rng, Verbs),
                        Status = task.IsOpen
                            ? (rng.NextDouble() < 0.4 ? TaskItemStatus.Completed : TaskItemStatus.NotStarted)
                            : TaskItemStatus.Completed,
                        Priority = Priority.None,
                        ProjectId = task.ProjectId,
                        ParentTaskId = task.Id,
                        Depth = 1,
                        CreatedAt = task.CreatedAt,
                        SortOrder = (i + 1) * SortOrderMath.Step,
                        DeletedAt = task.DeletedAt,
                    };
                    if (!child.IsOpen)
                    {
                        child.CompletedAt = task.CompletedAt ?? now.AddDays(-rng.Next(0, 30));
                    }
                    tasks.Add(child);
                    AddTags(rng, links, tags, child.Id);
                }
            }
        }

        db.Projects.AddRange(projects);
        db.Tags.AddRange(tags);
        db.RecurrenceRules.AddRange(rules);
        db.Tasks.AddRange(tasks);
        db.TaskTags.AddRange(links);
        await db.SaveChangesAsync(ct);

        hub.Publish(DataChangeKind.All);
        logger.LogInformation(
            "開発用データを入れました: タスク {Tasks} 件・タグ付け {Links} 件（{Elapsed} ms）",
            tasks.Count,
            links.Count,
            watch.ElapsedMilliseconds);
        return tasks.Count;
    }

    private static void AddTags(Random rng, List<TaskTag> links, IReadOnlyList<Tag> tags, Guid taskId)
    {
        if (rng.NextDouble() >= 0.4)
        {
            return;
        }
        var first = Pick(rng, tags);
        links.Add(new TaskTag { TaskId = taskId, TagId = first.Id });
        if (rng.NextDouble() < 0.4)
        {
            var second = Pick(rng, tags);
            if (second.Id != first.Id)
            {
                links.Add(new TaskTag { TaskId = taskId, TagId = second.Id });
            }
        }
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> values) => values[rng.Next(values.Count)];
}

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Data.ImportExport;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.ImportExport;

public sealed class JsonImporterTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid TagId = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid RuleId = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid TaskA = Guid.Parse("44444444-0000-0000-0000-00000000000a");
    private static readonly Guid TaskB = Guid.Parse("44444444-0000-0000-0000-00000000000b");
    private static readonly Guid TemplateId = Guid.Parse("55555555-0000-0000-0000-000000000001");
    private static readonly Guid ItemId = Guid.Parse("66666666-0000-0000-0000-000000000001");

    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
    private readonly List<DataWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    // ---- 往復 ----

    [Fact]
    public async Task Import_ReplaceWithOwnExport_RestoresEveryRow()
    {
        var world = NewWorld();
        await world.SeedAsync();
        var before = await world.DumpAsync();
        var json = await world.ExportAsync();
        // 書き出した後の変更（置換で消えるはず）
        await world.AddTaskAsync(new NewTaskRequest { Title = "後から足した", TagNames = ["後から"] });
        _clock.Advance(TimeSpan.FromHours(1));

        var result = await world.ImportAsync(json, ImportMode.Replace);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(before, await world.DumpAsync());
        // 検索キーは読み込み時に作り直される（全角で探しても当たる）
        var found = await world.Tasks.QueryAsync(new TaskQuery
        {
            Status = TaskStatusFilter.All,
            SearchText = "親タスク　２行目",
            IncludeSubtasks = false,
        });
        Assert.Equal("親タスク", Assert.Single(found).Task.Title);
    }

    [Fact]
    public async Task Import_ReplaceIntoAnotherDatabase_ReproducesSource()
    {
        var source = NewWorld();
        await source.SeedAsync();
        var target = NewWorld();
        await target.AddTaskAsync(new NewTaskRequest { Title = "移す先にあったもの" });

        var result = await target.ImportAsync(await source.ExportAsync(), ImportMode.Replace);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(await source.DumpAsync(), await target.DumpAsync());
        var summary = result.Value!;
        Assert.Equal(ImportMode.Replace, summary.Mode);
        Assert.Equal(11, summary.TaskCount);
        Assert.Equal(2, summary.ProjectCount);
        Assert.Equal(4, summary.TagCount);
        Assert.Equal(4, summary.TemplateCount);
        Assert.Empty(summary.Skipped);
    }

    [Fact]
    public async Task Import_Replace_TakesBackupBeforeWriting()
    {
        var world = NewWorld();
        await world.SeedAsync();
        var json = await world.ExportAsync();
        await world.AddTaskAsync(new NewTaskRequest { Title = "置換で消えるもの" });
        var countBefore = await world.CountTasksAsync();

        var result = await world.ImportAsync(json, ImportMode.Replace);

        // バックアップの時点では、まだ置き換える前のデータがそろっている
        Assert.Equal([countBefore], world.TasksAtBackup);
        Assert.Equal(world.BackupPath, result.Value!.BackupPath);
        Assert.Equal(countBefore - 1, await world.CountTasksAsync());
    }

    [Fact]
    public async Task Import_ReplaceWhenBackupFails_WritesNothing()
    {
        var world = NewWorld();
        await world.SeedAsync();
        var json = await world.ExportAsync();
        await world.AddTaskAsync(new NewTaskRequest { Title = "残るはずのもの" });
        var before = await world.DumpAsync();
        world.Published = DataChangeKind.None;
        world.BackupFailure = new IOException("ディスクがいっぱい");

        var result = await world.ImportAsync(json, ImportMode.Replace);

        Assert.False(result.Succeeded);
        Assert.Equal("置き換える前のバックアップをとれなかったので、置き換えをやめました。データは変わっていません。", result.Error);
        Assert.Equal(before, await world.DumpAsync());
        Assert.Equal(DataChangeKind.None, world.Published);
    }

    [Fact]
    public async Task Import_Append_DoesNotTakeBackup()
    {
        var world = NewWorld();
        await world.SeedAsync();

        var result = await world.ImportAsync(await world.ExportAsync(), ImportMode.Append);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(world.TasksAtBackup);
        Assert.Null(result.Value!.BackupPath);
    }

    [Theory]
    [InlineData(ImportMode.Append)]
    [InlineData(ImportMode.Replace)]
    public async Task Import_Succeeded_PublishesAllDataKinds(ImportMode mode)
    {
        var world = NewWorld();
        await world.SeedAsync();
        var json = await world.ExportAsync();
        world.Published = DataChangeKind.None;

        await world.ImportAsync(json, mode);

        Assert.Equal(
            DataChangeKind.Tasks | DataChangeKind.Projects | DataChangeKind.Tags | DataChangeKind.Templates,
            world.Published);
    }

    // ---- 追加 ----

    [Fact]
    public async Task Import_Append_AssignsNewIdsAndKeepsReferences()
    {
        var source = NewWorld();
        await source.SeedAsync();
        var target = NewWorld();
        var existing = await target.AddTaskAsync(new NewTaskRequest { Title = "前からあるもの" });

        var result = await target.ImportAsync(await source.ExportAsync(), ImportMode.Append);

        Assert.True(result.Succeeded, result.Error);
        await using var s = source.Db.CreateDbContext();
        await using var t = target.Db.CreateDbContext();
        var sourceIds = await s.Tasks.Select(x => x.Id).ToListAsync();
        var tasks = await t.Tasks.AsNoTracking().ToListAsync();
        Assert.Equal(12, tasks.Count);
        Assert.DoesNotContain(tasks, x => sourceIds.Contains(x.Id));

        // 親子（3階層）
        var parent = tasks.Single(x => x.Title == "親タスク");
        var child = tasks.Single(x => x.Title == "子タスク");
        var grandchild = tasks.Single(x => x.Title == "孫タスク");
        Assert.Equal(parent.Id, child.ParentTaskId);
        Assert.Equal(child.Id, grandchild.ParentTaskId);
        Assert.Equal([0, 1, 2], new[] { parent.Depth, child.Depth, grandchild.Depth });

        // ルートは前からあるものの後ろに、元の順のまま並ぶ
        var roots = tasks.Where(x => x.ParentTaskId is null && x.Id != existing.Id).ToList();
        Assert.All(roots, x => Assert.True(x.SortOrder > existing.SortOrder));
        Assert.True(parent.SortOrder < tasks.Single(x => x.Title == "終わったこと").SortOrder);

        // プロジェクト・タグ・繰り返し・系列・テンプレートの一群も付け替わっている
        var work = await t.Projects.SingleAsync(x => x.Name == "業務改善");
        Assert.Equal(work.Id, parent.ProjectId);
        var parentTags = await t.TaskTags.Where(x => x.TaskId == parent.Id && x.DeletedAt == null)
            .Join(t.Tags, x => x.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .ToListAsync();
        Assert.Equal(["Urgent", "仕事"], parentTags.Order(StringComparer.Ordinal));
        var weekly = tasks.Where(x => x.Title == "毎週の定例").ToList();
        Assert.Equal(2, weekly.Count);
        var seriesId = Assert.Single(weekly.Select(x => x.RecurrenceSeriesId).Distinct());
        Assert.Contains(weekly, x => x.Id == seriesId);
        var rule = await t.RecurrenceRules.SingleAsync();
        Assert.All(weekly, x => Assert.Equal(rule.Id, x.RecurrenceRuleId));
        Assert.Equal(1, rule.CompletedCount);
        var batch = tasks.Where(x => x.Title.StartsWith("展開した", StringComparison.Ordinal)).Select(x => x.TemplateBatchId).Distinct().ToList();
        Assert.NotNull(Assert.Single(batch));
        Assert.DoesNotContain(batch, id => s.Tasks.Any(x => x.TemplateBatchId == id));

        // テンプレート: 既定のプロジェクトとタグ、項目の親子とタグ
        var trip = await t.Templates.SingleAsync(x => x.Name == "出張の準備");
        var workTag = await t.Tags.SingleAsync(x => x.Name == "仕事");
        Assert.Equal(work.Id, trip.DefaultProjectId);
        Assert.Equal([workTag.Id], trip.DefaultTagIds);
        var items = await t.TemplateItems.Where(x => x.TemplateId == trip.Id).ToListAsync();
        var top = items.Single(x => x.Title == "準備");
        var middle = items.Single(x => x.Title == "持ち物");
        var bottom = items.Single(x => x.Title == "充電器");
        Assert.Equal(top.Id, middle.ParentItemId);
        Assert.Equal(middle.Id, bottom.ParentItemId);
        Assert.Equal(2, bottom.Depth);
        Assert.Equal([workTag.Id], top.TagIds);
        Assert.Equal(new TimeOnly(20, 0), top.DueTime);
    }

    [Fact]
    public async Task Import_AppendSameFileTwice_KeepsSeriesAndBatchesApart()
    {
        var world = NewWorld();
        await world.SeedAsync();
        var json = await world.ExportAsync();

        Assert.True((await world.ImportAsync(json, ImportMode.Append)).Succeeded);
        json.Position = 0;
        Assert.True((await world.ImportAsync(json, ImportMode.Append)).Succeeded);

        await using var db = world.Db.CreateDbContext();
        var weekly = await db.Tasks.Where(x => x.Title == "毎週の定例").ToListAsync();
        Assert.Equal(6, weekly.Count);
        Assert.Equal(3, weekly.Select(x => x.RecurrenceSeriesId).Distinct().Count());
        Assert.Equal(3, weekly.Select(x => x.RecurrenceRuleId).Distinct().Count());
        var batches = await db.Tasks.Where(x => x.TemplateBatchId != null).Select(x => x.TemplateBatchId).Distinct().CountAsync();
        Assert.Equal(3, batches);
        // 同じ名前のタグ・プロジェクト・テンプレートの親子は増えても、タグとプロジェクトは1つのまま
        Assert.Equal(1, await db.Tags.CountAsync(x => x.Name == "仕事" && x.DeletedAt == null));
        Assert.Equal(1, await db.Projects.CountAsync(x => x.Name == "業務改善" && x.DeletedAt == null));
    }

    [Fact]
    public async Task Import_Append_MergesTagsAndProjectsWithSameName()
    {
        var target = NewWorld();
        var existing = await target.AddTaskAsync(new NewTaskRequest { Title = "既存", TagNames = ["Work"], ProjectName = "業務改善" });
        var doc = ValidDocument();
        doc.Tags[0].Name = "＃ｗｏｒｋ";      // 全角・小文字・先頭の # でも同じタグ
        doc.Projects[0].Name = "  業務改善 ";

        var result = await target.ImportAsync(Serialize(doc), ImportMode.Append);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, result.Value!.MergedTagCount);
        Assert.Equal(1, result.Value.MergedProjectCount);
        Assert.Equal(0, result.Value.TagCount);
        Assert.Equal(0, result.Value.ProjectCount);
        await using var db = target.Db.CreateDbContext();
        var tag = await db.Tags.SingleAsync();
        var project = await db.Projects.SingleAsync();
        Assert.Equal("Work", tag.Name);
        var imported = await db.Tasks.SingleAsync(x => x.Title == "親");
        Assert.Equal(project.Id, imported.ProjectId);
        Assert.Equal(existing.ProjectId, project.Id);
        Assert.True(await db.TaskTags.AnyAsync(x => x.TaskId == imported.Id && x.TagId == tag.Id));
        var template = await db.Templates.SingleAsync();
        Assert.Equal(project.Id, template.DefaultProjectId);
        Assert.Equal([tag.Id], template.DefaultTagIds);
    }

    [Fact]
    public async Task Import_HandWrittenDocument_SucceedsInBothModes()
    {
        var world = NewWorld();

        var append = await world.ImportAsync(Serialize(ValidDocument()), ImportMode.Append);
        var replace = await world.ImportAsync(Serialize(ValidDocument()), ImportMode.Replace);

        Assert.True(append.Succeeded, append.Error);
        Assert.True(replace.Succeeded, replace.Error);
        await using var db = world.Db.CreateDbContext();
        var child = await db.Tasks.SingleAsync(x => x.Id == TaskB);
        Assert.Equal(TaskA, child.ParentTaskId);
        Assert.Equal(1, child.Depth);
        Assert.Equal(2, await db.Tasks.CountAsync());
    }

    [Fact]
    public async Task Import_TagsDifferingOnlyInNonAsciiCase_AreKeptApartLikeTheDatabase()
    {
        // DB の一意は NOCASE（ASCII だけ大文字小文字を同一視）なので「Ä」と「ä」は別のタグとして共存できる。その書き出しも読み込める
        var world = NewWorld();
        var doc = ValidDocument();
        doc.Tags.Add(new TagRecord { Id = Guid.NewGuid(), Name = "Äpfel" });
        doc.Tags.Add(new TagRecord { Id = Guid.NewGuid(), Name = "äpfel" });

        var result = await world.ImportAsync(Serialize(doc), ImportMode.Replace);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, result.Value!.TagCount);
    }

    [Fact]
    public async Task Import_TemplateReferencesUnknownTag_DropsItAndReportsSkipped()
    {
        var world = NewWorld();
        var doc = ValidDocument();
        doc.Templates[0].DefaultTagIds = [TagId, Guid.NewGuid()];

        var result = await world.ImportAsync(Serialize(doc), ImportMode.Replace);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("ファイルに無いタグ 1 件", Assert.Single(result.Value!.Skipped));
        await using var db = world.Db.CreateDbContext();
        Assert.Equal([TagId], (await db.Templates.SingleAsync()).DefaultTagIds);
    }

    [Fact]
    public async Task Import_DateOnlyDueFromAnotherTimeZone_KeepsCalendarDate()
    {
        var source = NewWorld();
        await source.SeedAsync();
        var utcMinus7 = TimeZoneInfo.CreateCustomTimeZone("UTC-7", TimeSpan.FromHours(-7), "UTC-7", "UTC-7");
        var target = NewWorld(new ZoneClock(_clock.UtcNow, utcMinus7));

        var result = await target.ImportAsync(await source.ExportAsync(), ImportMode.Append);

        Assert.True(result.Succeeded, result.Error);
        var rows = await target.Tasks.QueryAsync(new TaskQuery { Status = TaskStatusFilter.All });
        var dateOnly = rows.Single(r => r.Task.Title == "親タスク").Task;
        Assert.False(dateOnly.DueHasTime);
        Assert.Equal(new DateOnly(2026, 9, 25), target.Clock.ToLocalDate(dateOnly.DueAt!.Value));
        // 時刻ありはその瞬間のまま
        Assert.Equal(source.At(9, 24, 15, 30), rows.Single(r => r.Task.Title == "子タスク").Task.DueAt);
    }

    // ---- 検査で止める（何も書かない） ----

    [Theory]
    [InlineData("version-missing", "TaskDeck で書き出したファイルではありません")]
    [InlineData("version-newer", "新しい形式（版 2）")]
    [InlineData("task-missing-parent", "タスクの 2 件目: 親のタスク")]
    [InlineData("task-too-deep", "タスクの 4 件目: サブタスクが3階層を超えています")]
    [InlineData("task-cycle", "タスクの 1 件目: 親子の関係が循環しています")]
    [InlineData("task-empty-title", "タスクの 2 件目: タイトルがありません")]
    [InlineData("task-long-title", "タスクの 1 件目: タイトルが 500 文字を超えています")]
    [InlineData("task-missing-project", "タスクの 1 件目: プロジェクト（")]
    [InlineData("task-missing-rule", "タスクの 1 件目: 繰り返し（")]
    [InlineData("task-duplicate-id", "タスクの 2 件目: Id（")]
    [InlineData("task-bad-status", "タスクの 1 件目: 状態（status）の値（9）")]
    [InlineData("task-closed-without-date", "タスクの 1 件目: 完了・中止なのに")]
    [InlineData("tasktag-missing-tag", "タスクとタグの対応の 1 件目: タグ（")]
    [InlineData("tag-duplicate-name", "タグの 2 件目: 「仕事」と同じ名前のタグが 1 件目にもあります")]
    [InlineData("tag-long-name", "タグの 1 件目: 名前が 50 文字を超えています")]
    [InlineData("project-bad-color", "プロジェクトの 1 件目: 色（blue）")]
    [InlineData("rule-empty", "繰り返しの 1 件目: 規則（rrule）がありません")]
    [InlineData("template-missing-project", "テンプレートの 1 件目: 既定のプロジェクト（")]
    [InlineData("item-missing-template", "テンプレートの項目の 1 件目: テンプレート（")]
    [InlineData("item-parent-in-other-template", "テンプレートの項目の 2 件目: 親の項目（")]
    [InlineData("item-too-deep", "テンプレートの項目の 4 件目: 項目が3階層を超えています")]
    [InlineData("item-bad-time", "テンプレートの項目の 1 件目: 時刻（25:00）")]
    public async Task Import_InvalidDocument_FailsWithoutWriting(string breakage, string expected)
    {
        var world = NewWorld();
        await world.SeedAsync();
        var before = await world.DumpAsync();
        world.Published = DataChangeKind.None;
        var doc = ValidDocument();
        Break(doc, breakage);

        var result = await world.ImportAsync(Serialize(doc), ImportMode.Replace);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
        Assert.Equal(before, await world.DumpAsync());
        Assert.Empty(world.TasksAtBackup);
        Assert.Equal(DataChangeKind.None, world.Published);
    }

    [Fact]
    public async Task Import_BrokenJson_FailsWithLine()
    {
        var world = NewWorld();
        await world.SeedAsync();
        var before = await world.DumpAsync();

        var result = await world.ImportAsync(Utf8("{\n  \"formatVersion\": 1,\n  \"tasks\": [\n    { \"id\": "), ImportMode.Replace);

        Assert.False(result.Succeeded);
        Assert.StartsWith("JSON として読めませんでした（", result.Error, StringComparison.Ordinal);
        Assert.Contains("4 行目", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, await world.DumpAsync());
    }

    [Fact]
    public async Task Import_WrongValueType_NamesTheRow()
    {
        var world = NewWorld();
        const string json = """
            { "formatVersion": 1, "tasks": [
                { "id": "44444444-0000-0000-0000-00000000000a", "title": "a" },
                { "id": "not-a-guid", "title": "b" } ] }
            """;

        var result = await world.ImportAsync(Utf8(json), ImportMode.Append);

        Assert.False(result.Succeeded);
        Assert.Contains("タスクの 2 件目の id", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Import_NotAnExport_Fails(string json)
    {
        var world = NewWorld();

        var result = await world.ImportAsync(Utf8(json), ImportMode.Append);

        Assert.False(result.Succeeded);
        Assert.Contains("TaskDeck で書き出したファイルではありません", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_MoreTasksThanLimit_Fails()
    {
        var world = NewWorld();

        var result = await world.ImportAsync(Serialize(ValidDocument()), ImportMode.Append, world.NewImporter(maxTasks: 1));

        Assert.False(result.Succeeded);
        Assert.Equal("タスクが 2 件あります。一度に読み込めるのは 1 件までです。", result.Error);
        Assert.Equal(0, await world.CountTasksAsync());
    }

    [Fact]
    public async Task Import_FileLargerThanLimit_Fails()
    {
        var world = NewWorld();

        var result = await world.ImportAsync(Serialize(ValidDocument()), ImportMode.Append, world.NewImporter(maxFileBytes: 100));

        Assert.False(result.Succeeded);
        Assert.StartsWith("ファイルが大きすぎます", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, await world.CountTasksAsync());
    }

    [Fact]
    public async Task Import_CancelledWhileWriting_LeavesDataUnchanged()
    {
        var world = NewWorld();
        await world.SeedAsync();
        var before = await world.DumpAsync();
        var importer = world.NewImporter(chunkSize: 5);
        var read = await importer.ReadAsync(await world.ExportAsync());
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Done > 0)
            {
                cts.Cancel();   // 1かたまり書いたところで取り消す
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => importer.ImportAsync(read.Value!, ImportMode.Replace, progress, cts.Token));

        Assert.Equal(before, await world.DumpAsync());
    }

    // ---- 部品 ----

    private DataWorld NewWorld(IClock? clock = null)
    {
        var world = new DataWorld(clock ?? _clock);
        _worlds.Add(world);
        return world;
    }

    /// <summary>手で書いた最小の正しいファイル（親子・プロジェクト・タグ・繰り返し・テンプレート）。</summary>
    private static ExportDocument ValidDocument()
    {
        var created = FixedClock.LocalToUtc(2026, 9, 1);
        return new ExportDocument
        {
            FormatVersion = 1,
            ExportedAt = created,
            Projects = [new ProjectRecord { Id = ProjectId, Name = "業務改善", ColorHex = "#0067C0", SortOrder = 1024, CreatedAt = created }],
            Tags = [new TagRecord { Id = TagId, Name = "仕事", CreatedAt = created }],
            RecurrenceRules = [new RecurrenceRuleRecord { Id = RuleId, RRule = "FREQ=DAILY", AnchorAt = created, CreatedAt = created }],
            Tasks =
            [
                new TaskRecord { Id = TaskA, Title = "親", ProjectId = ProjectId, RecurrenceRuleId = RuleId, SortOrder = 1024, CreatedAt = created },
                new TaskRecord { Id = TaskB, Title = "子", ParentTaskId = TaskA, SortOrder = 1024, CreatedAt = created },
            ],
            TaskTags = [new TaskTagRecord { TaskId = TaskA, TagId = TagId, CreatedAt = created }],
            Templates = [new TemplateRecord { Id = TemplateId, Name = "型", DefaultProjectId = ProjectId, DefaultTagIds = [TagId], SortOrder = 1024, CreatedAt = created }],
            TemplateItems = [new TemplateItemRecord { Id = ItemId, TemplateId = TemplateId, Title = "項目", DueTime = "09:00", SortOrder = 1024, CreatedAt = created }],
        };
    }

    private static void Break(ExportDocument doc, string breakage)
    {
        switch (breakage)
        {
            case "version-missing":
                doc.FormatVersion = 0;
                break;
            case "version-newer":
                doc.FormatVersion = 2;
                break;
            case "task-missing-parent":
                doc.Tasks[1].ParentTaskId = Guid.NewGuid();
                break;
            case "task-too-deep":
                var c = new TaskRecord { Id = Guid.NewGuid(), Title = "孫", ParentTaskId = TaskB };
                doc.Tasks.Add(c);
                doc.Tasks.Add(new TaskRecord { Id = Guid.NewGuid(), Title = "ひ孫", ParentTaskId = c.Id });
                break;
            case "task-cycle":
                doc.Tasks[0].ParentTaskId = TaskB;
                break;
            case "task-empty-title":
                doc.Tasks[1].Title = " \n ";
                break;
            case "task-long-title":
                doc.Tasks[0].Title = new string('あ', 501);
                break;
            case "task-missing-project":
                doc.Tasks[0].ProjectId = Guid.NewGuid();
                break;
            case "task-missing-rule":
                doc.Tasks[0].RecurrenceRuleId = Guid.NewGuid();
                break;
            case "task-duplicate-id":
                doc.Tasks[1].Id = TaskA;
                doc.Tasks[1].ParentTaskId = null;
                break;
            case "task-bad-status":
                doc.Tasks[0].Status = (TaskItemStatus)9;
                break;
            case "task-closed-without-date":
                doc.Tasks[0].Status = TaskItemStatus.Completed;
                break;
            case "tasktag-missing-tag":
                doc.TaskTags[0].TagId = Guid.NewGuid();
                break;
            case "tag-duplicate-name":
                doc.Tags.Add(new TagRecord { Id = Guid.NewGuid(), Name = "仕事" });
                break;
            case "tag-long-name":
                doc.Tags[0].Name = new string('a', 51);
                break;
            case "project-bad-color":
                doc.Projects[0].ColorHex = "blue";
                break;
            case "rule-empty":
                doc.RecurrenceRules[0].RRule = " ";
                break;
            case "template-missing-project":
                doc.Templates[0].DefaultProjectId = Guid.NewGuid();
                break;
            case "item-missing-template":
                doc.TemplateItems[0].TemplateId = Guid.NewGuid();
                break;
            case "item-parent-in-other-template":
                var other = new TemplateRecord { Id = Guid.NewGuid(), Name = "別の型" };
                doc.Templates.Add(other);
                doc.TemplateItems.Add(new TemplateItemRecord { Id = Guid.NewGuid(), TemplateId = other.Id, Title = "子", ParentItemId = ItemId });
                break;
            case "item-too-deep":
                var parent = ItemId;
                for (var i = 0; i < 3; i++)
                {
                    var item = new TemplateItemRecord { Id = Guid.NewGuid(), TemplateId = TemplateId, Title = $"段{i}", ParentItemId = parent };
                    doc.TemplateItems.Add(item);
                    parent = item.Id;
                }
                break;
            case "item-bad-time":
                doc.TemplateItems[0].DueTime = "25:00";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(breakage), breakage, "知らない壊し方");
        }
    }

    private static MemoryStream Serialize(ExportDocument doc) => new(JsonSerializer.SerializeToUtf8Bytes(doc, ExportJson.Options));

    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    private sealed class SyncProgress(Action<DataTransferProgress> report) : IProgress<DataTransferProgress>
    {
        public void Report(DataTransferProgress value) => report(value);
    }

    private sealed class ZoneClock(DateTime utcNow, TimeZoneInfo zone) : IClock
    {
        public DateTime UtcNow { get; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

        public TimeZoneInfo LocalTimeZone { get; } = zone;
    }
}

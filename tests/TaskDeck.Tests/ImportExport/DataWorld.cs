using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Data.ImportExport;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.ImportExport;

/// <summary>
/// 入出力のテストの土台: インメモリ DB 1つぶんのリポジトリと書き出し・読み込み。
/// 書き出し元と読み込み先を別の DB にしたいときは2つ作る。
/// </summary>
internal sealed class DataWorld : IDisposable
{
    /// <summary>往復で保たない列（読み込んだ時点の値になる。報告に書いた判断）。</summary>
    private static readonly HashSet<string> NotKept = ["UpdatedAt", "SyncState", "RemoteUpdatedAt"];

    public DataWorld(IClock clock)
    {
        Clock = clock;
        Db = new TestDatabase(clock);
        Tasks = new TaskRepository(Db, clock, Engine, Settings, Hub, NullLogger<TaskRepository>.Instance);
        Projects = new ProjectRepository(Db, clock, Hub);
        Tags = new TagRepository(Db, clock, Hub);
        Templates = new TemplateRepository(Db, clock, Settings, Hub);
        Exporter = new JsonExporter(Db, clock, NullLogger<JsonExporter>.Instance);
        Importer = NewImporter();
        Hub.Changed += (_, e) => Published |= e.Kinds;
    }

    public IClock Clock { get; }

    public TestDatabase Db { get; }

    public DataChangeHub Hub { get; } = new();

    public FakeSettingsStore Settings { get; } = new();

    public IRecurrenceEngine Engine { get; } = Substitute.For<IRecurrenceEngine>();

    public TaskRepository Tasks { get; }

    public ProjectRepository Projects { get; }

    public TagRepository Tags { get; }

    public TemplateRepository Templates { get; }

    public JsonExporter Exporter { get; }

    public JsonImporter Importer { get; }

    /// <summary>変更通知で届いた種類（テストの途中で None に戻して使う）。</summary>
    public DataChangeKind Published { get; set; }

    /// <summary>置換の前のバックアップが呼ばれるたびに、その時点のタスク件数を積む（まだ書き換える前かを確かめる）。</summary>
    public List<int> TasksAtBackup { get; } = [];

    public string BackupPath { get; } = @"backups\taskdeck_20260922_100000.db";

    /// <summary>置き換える前のバックアップで起こす例外（ディスクがいっぱい等の再現）。null なら成功する。</summary>
    public Exception? BackupFailure { get; set; }

    public JsonImporter NewImporter(
        long maxFileBytes = JsonImporter.DefaultMaxFileBytes,
        int maxTasks = JsonImporter.DefaultMaxTasks,
        int chunkSize = 2000) =>
        new(Db, Clock, Hub, NullLogger<JsonImporter>.Instance, OnBackup)
        {
            MaxFileBytes = maxFileBytes,
            MaxTasks = maxTasks,
            ChunkSize = chunkSize,
        };

    public DateTime Day(int month, int day) => Clock.LocalDayStartUtc(new DateOnly(2026, month, day));

    public DateTime At(int month, int day, int hour, int minute = 0) =>
        Clock.LocalToUtc(new DateOnly(2026, month, day), new TimeOnly(hour, minute));

    public async Task<TaskItem> AddTaskAsync(NewTaskRequest request) => (await Tasks.AddAsync(request)).Created[0];

    public async Task<MemoryStream> ExportAsync()
    {
        var stream = new MemoryStream();
        await Exporter.ExportAsync(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>読んで検査し、通れば書き込む（画面と同じ2段階）。</summary>
    public async Task<OperationResult<ImportSummary>> ImportAsync(Stream json, ImportMode mode, JsonImporter? importer = null)
    {
        importer ??= Importer;
        var read = await importer.ReadAsync(json);
        return read.Succeeded
            ? await importer.ImportAsync(read.Value!, mode)
            : OperationResult<ImportSummary>.Fail(read.Error!);
    }

    public async Task<int> CountTasksAsync()
    {
        await using var db = Db.CreateDbContext();
        return await db.Tasks.CountAsync();
    }

    /// <summary>
    /// 全表の全行を「列名=値」で並べた文字列（行は並べ替えてある）。UpdatedAt・SyncState・RemoteUpdatedAt 以外の列は全部比べる
    /// （エンティティに列が増えて、書き出しに足し忘れたら往復のテストが落ちる）。
    /// </summary>
    public async Task<string> DumpAsync()
    {
        await using var db = Db.CreateDbContext();
        return string.Join(
            "\n---\n",
            Rows(await db.Projects.AsNoTracking().ToListAsync()),
            Rows(await db.Tags.AsNoTracking().ToListAsync()),
            Rows(await db.RecurrenceRules.AsNoTracking().ToListAsync()),
            Rows(await db.Tasks.AsNoTracking().ToListAsync()),
            Rows(await db.TaskTags.AsNoTracking().ToListAsync()),
            Rows(await db.Templates.AsNoTracking().ToListAsync()),
            Rows(await db.TemplateItems.AsNoTracking().ToListAsync()));
    }

    /// <summary>
    /// ひととおりの形を持つデータ: 3階層・完了・中止・ゴミ箱・時刻あり／日付のみ・相対通知と通知済み・所要時間・
    /// 繰り返し（完了して次回ができた系列）・外したタグと消したタグの名残・消したプロジェクトとアーカイブ・
    /// テンプレートから作った一群・初期テンプレートと3階層の自前テンプレート。
    /// </summary>
    public async Task SeedAsync()
    {
        await Templates.SeedDefaultsAsync();
        var work = await Projects.AddAsync("業務改善");
        var old = await Projects.AddAsync("旧案件");
        await Projects.UpdateAsync(old.Id, p => p.IsArchived = true);
        var dropped = await Projects.AddAsync("やめた案件");
        await Projects.DeleteAsync(dropped.Id);

        var parent = await AddTaskAsync(new NewTaskRequest
        {
            Title = "親タスク",
            Notes = "メモ\n2行目",
            ProjectId = work.Id,
            Priority = Priority.High,
            DueAt = Day(9, 25),
            TagNames = ["仕事", "Urgent"],
        });
        var child = await AddTaskAsync(new NewTaskRequest
        {
            Title = "子タスク",
            ParentTaskId = parent.Id,
            DueAt = At(9, 24, 15, 30),
            DueHasTime = true,
            RemindOffsetMinutes = 30,
            DurationMinutes = 45,
        });
        await AddTaskAsync(new NewTaskRequest { Title = "孫タスク", ParentTaskId = child.Id, Status = TaskItemStatus.InProgress });
        await AddTaskAsync(new NewTaskRequest { Title = "終わったこと", Status = TaskItemStatus.Completed, ProjectId = old.Id });
        await AddTaskAsync(new NewTaskRequest { Title = "やめたこと", Status = TaskItemStatus.Cancelled });
        var trashed = await AddTaskAsync(new NewTaskRequest { Title = "ゴミ箱のタスク", TagNames = ["仕事"] });
        await Tasks.SoftDeleteAsync([trashed.Id]);
        await Tasks.MarkNotifiedAsync([child.Id], At(9, 24, 15));   // リマインド（期限の30分前）を出した時刻

        // 完了すると同じ系列に次回ができ、ルールの完了回数が増える
        Engine.NextDue(Arg.Any<TaskItem>(), Arg.Any<RecurrenceRule>(), Arg.Any<DateTime>()).Returns(Day(9, 28));
        var weekly = await AddTaskAsync(new NewTaskRequest
        {
            Title = "毎週の定例",
            DueAt = Day(9, 21),
            Recurrence = new RecurrenceInput(
                RecurrencePresets.Weekly([DayOfWeek.Monday]),
                EndKind: RecurrenceEndKind.UntilDate,
                EndDate: new DateOnly(2026, 12, 31)),
        });
        await Tasks.SetCompletedAsync([weekly.Id], true);

        // 外したタグ（TaskTag の名残）と、消したタグ（Tag とその TaskTag の名残）
        var retagged = await AddTaskAsync(new NewTaskRequest { Title = "タグを付け替えたもの", TagNames = ["会議", "使わない"] });
        var meeting = (await Tags.GetAllAsync()).Single(t => t.Name == "会議");
        await Tasks.SetTagsAsync(retagged.Id, [meeting.Id]);
        var removed = await Tags.GetOrCreateAsync("消すタグ");
        await Tasks.AddTagsAsync([retagged.Id], [removed.Id]);
        await Tags.DeleteAsync(removed.Id);

        var batch = Guid.CreateVersion7();
        await AddTaskAsync(new NewTaskRequest { Title = "展開した1", TemplateBatchId = batch });
        await AddTaskAsync(new NewTaskRequest { Title = "展開した2", TemplateBatchId = batch });

        var workTag = (await Tags.GetAllAsync()).Single(t => t.Name == "仕事");
        var top = new TaskTemplateItem { Title = "準備", DueOffsetDays = -1, DueTime = new TimeOnly(20, 0), SortOrder = 1024, TagIds = [workTag.Id] };
        var middle = new TaskTemplateItem { Title = "持ち物", ParentItemId = top.Id, SortOrder = 1024, DurationMinutes = 15 };
        var bottom = new TaskTemplateItem { Title = "充電器", ParentItemId = middle.Id, SortOrder = 1024, Priority = Priority.Low };
        await Templates.SaveAsync(
            new TaskTemplate { Name = "出張の準備", AnchorLabel = "出発日", DefaultProjectId = work.Id, DefaultTagIds = [workTag.Id] },
            [top, middle, bottom]);
    }

    public void Dispose() => Db.Dispose();

    private string? OnBackup()
    {
        using var db = Db.CreateDbContext();
        TasksAtBackup.Add(db.Tasks.Count());
        return BackupFailure is null ? BackupPath : throw BackupFailure;
    }

    private static string Rows<T>(IEnumerable<T> rows)
    {
        var properties = typeof(T).GetProperties().Where(p => p.CanWrite && !NotKept.Contains(p.Name)).ToList();
        return string.Join(
            "\n",
            rows.Select(row => string.Join(" | ", properties.Select(p => $"{p.Name}={Format(p.GetValue(row))}")))
                .Order(StringComparer.Ordinal));
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        double x => x.ToString("R", CultureInfo.InvariantCulture),
        IEnumerable<Guid> ids => "[" + string.Join(",", ids) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

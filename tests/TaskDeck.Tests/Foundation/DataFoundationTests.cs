using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Data;
using TaskDeck.Data.Backup;
using TaskDeck.Data.Infrastructure;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Foundation;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taskdeck-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private sealed class FileDbFactory(DbContextOptions<TaskDeckDbContext> options) : IDbContextFactory<TaskDeckDbContext>
    {
        public TaskDeckDbContext CreateDbContext() => new(options);
    }

    private (DatabaseInitializer Init, TaskDeckDataOptions Options) Create()
    {
        var options = new TaskDeckDataOptions(Path.Combine(_dir, "taskdeck.db"), Path.Combine(_dir, "backups"));
        var dbOptions = new DbContextOptionsBuilder<TaskDeckDbContext>()
            .UseSqlite(TaskDeckDataOptions.BuildConnectionString(options.DatabasePath, pooling: false))
            .AddInterceptors(new AuditInterceptor(_clock))
            .Options;
        var backup = new BackupService(options, _clock, NullLogger<BackupService>.Instance);
        return (new DatabaseInitializer(new FileDbFactory(dbOptions), backup, options, NullLogger<DatabaseInitializer>.Instance), options);
    }

    [Fact]
    public async Task Initialize_FirstRun_CreatesDatabaseWithoutBackup()
    {
        var (init, options) = Create();

        var result = await init.InitializeAsync();

        Assert.True(result.Created);
        Assert.True(File.Exists(options.DatabasePath));
        Assert.Null(result.BackupPath);
    }

    [Fact]
    public async Task Initialize_SecondRun_TakesBackupAndKeepsFiveGenerations()
    {
        var (init, options) = Create();
        await init.InitializeAsync();

        for (var i = 0; i < 7; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            var result = await init.InitializeAsync();
            Assert.False(result.Created);
            Assert.NotNull(result.BackupPath);
        }

        Assert.Equal(BackupService.Generations, Directory.GetFiles(options.BackupDirectory, "taskdeck_*.db").Length);
    }

    [Fact]
    public async Task Initialize_UpToDateAndDeferRequested_LeavesBackupToCaller()
    {
        var (init, options) = Create();
        await init.InitializeAsync();

        var result = await init.InitializeAsync(deferBackupWhenCurrent: true);

        Assert.True(result.BackupDeferred);
        Assert.Null(result.BackupPath);
        Assert.False(Directory.Exists(options.BackupDirectory) && Directory.GetFiles(options.BackupDirectory).Length > 0);
    }

    [Fact]
    public async Task Initialize_MigrationFailsWithDeferRequested_StillBacksUpBeforeMigrating()
    {
        var (init, options) = Create();
        Directory.CreateDirectory(_dir);
        using (var conn = new SqliteConnection(TaskDeckDataOptions.BuildConnectionString(options.DatabasePath, pooling: false)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE TaskItem (Sentinel TEXT);";
            cmd.ExecuteNonQuery();
        }

        var ex = await Assert.ThrowsAsync<DatabaseMigrationException>(() => init.InitializeAsync(deferBackupWhenCurrent: true));

        Assert.NotNull(ex.BackupPath);
    }

    [Fact]
    public async Task Initialize_MigrationFails_RestoresOriginalAndThrows()
    {
        var (init, options) = Create();
        Directory.CreateDirectory(_dir);
        // 同名の表がある「壊れた」DB を置き、マイグレーションを失敗させる
        using (var conn = new SqliteConnection(TaskDeckDataOptions.BuildConnectionString(options.DatabasePath, pooling: false)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE TaskItem (Sentinel TEXT); INSERT INTO TaskItem VALUES ('keep-me');";
            cmd.ExecuteNonQuery();
        }

        var ex = await Assert.ThrowsAsync<DatabaseMigrationException>(() => init.InitializeAsync());

        Assert.NotNull(ex.BackupPath);
        using var check = new SqliteConnection(TaskDeckDataOptions.BuildConnectionString(options.DatabasePath, pooling: false));
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT Sentinel FROM TaskItem;";
        Assert.Equal("keep-me", read.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

public sealed class TaskRepositoryBasicsTests : IDisposable
{
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);
    private readonly TestDatabase _db;
    private readonly TaskRepository _repo;
    private readonly DataChangeHub _hub = new();

    public TaskRepositoryBasicsTests()
    {
        _db = new TestDatabase(_clock);
        _repo = new TaskRepository(_db, _clock, Substitute.For<IRecurrenceEngine>(), new FakeSettingsStore(), _hub, NullLogger<TaskRepository>.Instance);
    }

    private DateTime Day(int month, int day) => _clock.LocalDayStartUtc(new DateOnly(2026, month, day));

    [Fact]
    public async Task Add_WithProjectAndTagNames_CreatesThemOnce()
    {
        await _repo.AddAsync(new NewTaskRequest { Title = "a", ProjectName = "業務改善", TagNames = ["仕事", "＃仕事"] });
        await _repo.AddAsync(new NewTaskRequest { Title = "b", ProjectName = "業務改善", TagNames = ["仕事"] });

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Projects.CountAsync());
        Assert.Equal(1, await db.Tags.CountAsync());
        Assert.Equal(2, await db.TaskTags.CountAsync());
    }

    [Fact]
    public async Task Query_Today_ContainsOverdueAndToday_NotTomorrow()
    {
        await _repo.AddAsync(new NewTaskRequest { Title = "yesterday", DueAt = Day(9, 21) });
        await _repo.AddAsync(new NewTaskRequest { Title = "today", DueAt = Day(9, 22) });
        await _repo.AddAsync(new NewTaskRequest { Title = "tomorrow", DueAt = Day(9, 23) });
        await _repo.AddAsync(new NewTaskRequest { Title = "none" });

        var rows = await _repo.QueryAsync(BuiltInViews.QueryFor(ViewKey.Today));

        Assert.Equal(["yesterday", "today"], rows.Select(r => r.Task.Title));
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.Task.DueAt!.Value.Kind));
        var counts = await _repo.GetViewCountsAsync();
        Assert.Equal(2, counts.Today);
        Assert.Equal(1, counts.Overdue);
        Assert.Equal(4, counts.AllOpen);
    }

    [Fact]
    public async Task Query_IncludeSubtasks_AddsDescendantsAsContext()
    {
        var parent = (await _repo.AddAsync(new NewTaskRequest { Title = "parent", DueAt = Day(9, 22) })).Created[0];
        var child = (await _repo.AddAsync(new NewTaskRequest { Title = "child", ParentTaskId = parent.Id })).Created[0];

        var rows = await _repo.QueryAsync(BuiltInViews.QueryFor(ViewKey.Today));

        Assert.Equal(2, rows.Count);
        Assert.True(rows.Single(r => r.Task.Id == child.Id).IsContext);
        Assert.Equal(1, rows.Single(r => r.Task.Id == parent.Id).SubtaskCount);
        Assert.Equal(1, child.Depth);
    }

    [Fact]
    public async Task Search_FullWidthInput_MatchesHalfWidthTitle()
    {
        await _repo.AddAsync(new NewTaskRequest { Title = "AWS課題 EFS編" });
        await _repo.AddAsync(new NewTaskRequest { Title = "別件", Notes = "aws のメモ" });
        await _repo.AddAsync(new NewTaskRequest { Title = "関係ない" });

        var rows = await _repo.QueryAsync(new TaskQuery { SearchText = "ＡＷＳ" });

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Complete_ThenUndo_RestoresOpenState()
    {
        var task = (await _repo.AddAsync(new NewTaskRequest { Title = "x" })).Created[0];

        var result = await _repo.SetCompletedAsync([task.Id], true);
        var done = await _repo.GetAsync(task.Id);
        Assert.Equal(TaskItemStatus.Completed, done!.Status);
        Assert.Equal(_clock.UtcNow, done.CompletedAt);

        await _repo.ApplyUndoAsync(result.Changes);
        var back = await _repo.GetAsync(task.Id);
        Assert.Equal(TaskItemStatus.NotStarted, back!.Status);
        Assert.Null(back.CompletedAt);
    }

    [Fact]
    public async Task Complete_Recurring_CreatesNextAndUndoRemovesIt()
    {
        var engine = Substitute.For<IRecurrenceEngine>();
        engine.NextDue(Arg.Any<TaskItem>(), Arg.Any<RecurrenceRule>(), Arg.Any<DateTime>()).Returns(Day(9, 29));
        var repo = new TaskRepository(_db, _clock, engine, new FakeSettingsStore(), _hub, NullLogger<TaskRepository>.Instance);
        var task = (await repo.AddAsync(new NewTaskRequest
        {
            Title = "weekly",
            DueAt = Day(9, 22),
            TagNames = ["仕事"],
            Recurrence = new RecurrenceInput(RecurrencePresets.Weekly([DayOfWeek.Tuesday])),
        })).Created[0];

        var result = await repo.SetCompletedAsync([task.Id], true);
        var next = Assert.Single(result.Created);
        Assert.Equal(Day(9, 29), next.DueAt);
        Assert.Equal(task.RecurrenceSeriesId, next.RecurrenceSeriesId);

        // 同じ日にもう一度完了操作しても2件目は作らない
        var again = await repo.SetCompletedAsync([task.Id], true);
        Assert.Empty(again.Created);

        await repo.ApplyUndoAsync(result.Changes);
        Assert.Null(await repo.GetAsync(next.Id));
        Assert.Equal(TaskItemStatus.NotStarted, (await repo.GetAsync(task.Id))!.Status);
        await using var db = _db.CreateDbContext();
        Assert.Equal(0, (await db.RecurrenceRules.SingleAsync()).CompletedCount);
    }

    [Fact]
    public async Task SoftDelete_Parent_PromotesChild_AndUndoReattaches()
    {
        var parent = (await _repo.AddAsync(new NewTaskRequest { Title = "parent" })).Created[0];
        var child = (await _repo.AddAsync(new NewTaskRequest { Title = "child", ParentTaskId = parent.Id })).Created[0];

        var result = await _repo.SoftDeleteAsync([parent.Id]);
        var promoted = await _repo.GetAsync(child.Id);
        Assert.Null(promoted!.ParentTaskId);
        Assert.Equal(0, promoted.Depth);

        await _repo.ApplyUndoAsync(result.Changes);
        var restored = await _repo.GetAsync(child.Id);
        Assert.Equal(parent.Id, restored!.ParentTaskId);
        Assert.Equal(1, restored.Depth);
        Assert.Null((await _repo.GetAsync(parent.Id))!.DeletedAt);
    }

    [Fact]
    public async Task Update_EmptyTitle_KeepsPreviousTitle()
    {
        var task = (await _repo.AddAsync(new NewTaskRequest { Title = "keep" })).Created[0];

        await _repo.UpdateAsync(task.Id, t => t.Title = "   ");

        Assert.Equal("keep", (await _repo.GetAsync(task.Id))!.Title);
    }

    [Fact]
    public async Task Update_ForbiddenColumn_Throws()
    {
        var task = (await _repo.AddAsync(new NewTaskRequest { Title = "x" })).Created[0];

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.UpdateAsync(task.Id, t => t.SortOrder = 99));
    }

    [Fact]
    public async Task Writes_PublishChangeNotification()
    {
        var kinds = DataChangeKind.None;
        _hub.Changed += (_, e) => kinds |= e.Kinds;

        await _repo.AddAsync(new NewTaskRequest { Title = "x", TagNames = ["new"] });

        Assert.True(kinds.HasFlag(DataChangeKind.Tasks));
        Assert.True(kinds.HasFlag(DataChangeKind.Tags));
    }

    public void Dispose() => _db.Dispose();
}

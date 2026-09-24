using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Data.Maintenance;
using TaskDeck.Data.Seeding;

namespace TaskDeck.Tests.Data;

public sealed class DatabaseMaintenanceTests : DataTestBase
{
    private DatabaseMaintenance Maintenance =>
        new(Db, Tasks, State, Clock, NullLogger<DatabaseMaintenance>.Instance);

    [Fact]
    public async Task RunStartup_TrashOlderThanThirtyDays_IsPurged()
    {
        var old = await AddAsync("31日前に消した");
        await Tasks.SoftDeleteAsync([old.Id]);
        Clock.Advance(TimeSpan.FromDays(31));
        var recent = await AddAsync("今日消した");
        await Tasks.SoftDeleteAsync([recent.Id]);

        await Maintenance.RunStartupAsync();

        Assert.Null(await Tasks.GetAsync(old.Id));
        Assert.NotNull(await Tasks.GetAsync(recent.Id));
        Assert.Equal("2026-10-23", await State.GetAsync(AppStateKeys.LastVacuumDate));
    }

    [Fact]
    public async Task RunStartup_VacuumedWithinAMonth_IsSkipped()
    {
        await State.SetAsync(AppStateKeys.LastVacuumDate, "2026-09-10");

        await Maintenance.RunStartupAsync();

        Assert.Equal("2026-09-10", await State.GetAsync(AppStateKeys.LastVacuumDate));
    }

    [Fact]
    public async Task RunStartup_VacuumedAMonthAgo_RunsAgain()
    {
        await State.SetAsync(AppStateKeys.LastVacuumDate, "2026-08-22");

        await Maintenance.RunStartupAsync();

        Assert.Equal("2026-09-22", await State.GetAsync(AppStateKeys.LastVacuumDate));
    }
}

public sealed class DataSeederTests : DataTestBase
{
    private SampleDataSeeder Samples =>
        new(Db, Tasks, State, Clock, NullLogger<SampleDataSeeder>.Instance);

    [Fact]
    public async Task SampleData_FirstRunOnly_AddsExamples()
    {
        Assert.True(await Samples.SeedIfNeededAsync());

        var rows = await Tasks.QueryAsync(new TaskQuery());
        Assert.Equal(5, rows.Count);
        Assert.Equal(Day(9, 22), rows.Single(r => r.Task.Title == "今日やることを1つ決める").Task.DueAt);
        Assert.Equal(2, rows.Single(r => r.Task.Title == "机まわりを片づける").SubtaskCount);
        Assert.Single(rows.Single(r => r.Task.Title == "タグで分類してみる").TagIds);

        Assert.False(await Samples.SeedIfNeededAsync());
        Assert.Equal(5, (await Tasks.QueryAsync(new TaskQuery())).Count);
    }

    [Fact]
    public async Task SampleData_WhenTasksAlreadyExist_IsSkipped()
    {
        await AddAsync("自分で作ったタスク");

        Assert.False(await Samples.SeedIfNeededAsync());

        Assert.Single(await Tasks.QueryAsync(new TaskQuery()));
        Assert.NotNull(await State.GetAsync(AppStateKeys.SampleDataSeeded));
    }

    [Fact]
    public async Task DevData_Seed_CreatesRequestedCountWithProjectsTagsAndSubtasks()
    {
        var seeder = new DevDataSeeder(Db, Clock, Hub, NullLogger<DevDataSeeder>.Instance);

        var created = await seeder.SeedAsync(500, randomSeed: 7);

        Assert.Equal(500, created);
        await using var db = Db.CreateDbContext();
        Assert.Equal(500, await db.Tasks.CountAsync());
        Assert.Equal(10, await db.Projects.CountAsync());
        Assert.Equal(20, await db.Tags.CountAsync());
        Assert.True(await db.Tasks.AnyAsync(t => t.ParentTaskId != null));
        Assert.True(await db.TaskTags.AnyAsync());
        var open = await db.Tasks.CountAsync(t => t.DeletedAt == null
            && (t.Status == TaskItemStatus.NotStarted || t.Status == TaskItemStatus.InProgress));
        Assert.InRange(open, 80, 220);
        // 検索キー（シャドウプロパティ）も埋まっている＝そのまま検索の計測に使える
        Assert.NotEmpty(await Tasks.QueryAsync(new TaskQuery { Status = TaskStatusFilter.All, SearchText = "会議" }));
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Data;

/// <summary>
/// Data 層のテストの土台。JST 固定の時計＋インメモリ SQLite（本物のマイグレーション）で、
/// IRecurrenceEngine は NSubstitute に差し替える（本実装は波1-B が並行して作っている）。
/// </summary>
public abstract class DataTestBase : IDisposable
{
    protected DataTestBase()
    {
        Db = new TestDatabase(Clock);
        Tasks = new TaskRepository(Db, Clock, Engine, Settings, Hub, NullLogger<TaskRepository>.Instance);
        Templates = new TemplateRepository(Db, Clock, Settings, Hub);
        Tags = new TagRepository(Db, Clock, Hub);
        State = new AppStateRepository(Db);
    }

    protected FixedClock Clock { get; } = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    protected TestDatabase Db { get; }

    protected DataChangeHub Hub { get; } = new();

    protected FakeSettingsStore Settings { get; } = new();

    protected IRecurrenceEngine Engine { get; } = Substitute.For<IRecurrenceEngine>();

    protected TaskRepository Tasks { get; }

    protected TemplateRepository Templates { get; }

    protected TagRepository Tags { get; }

    protected AppStateRepository State { get; }

    /// <summary>2026年のローカル日付の 0:00（日付のみの期限の保存形）。</summary>
    protected DateTime Day(int month, int day) => Clock.LocalDayStartUtc(new DateOnly(2026, month, day));

    /// <summary>2026年のローカル日時（時刻ありの期限）。</summary>
    protected DateTime At(int month, int day, int hour, int minute = 0) =>
        Clock.LocalToUtc(new DateOnly(2026, month, day), new TimeOnly(hour, minute));

    protected async Task<TaskItem> AddAsync(
        string title,
        DateTime? due = null,
        bool dueHasTime = false,
        Guid? parentId = null,
        IReadOnlyList<string>? tagNames = null,
        TaskItemStatus status = TaskItemStatus.NotStarted,
        Priority priority = Priority.None,
        string? projectName = null,
        int? remindOffsetMinutes = null,
        RecurrenceInput? recurrence = null)
    {
        var result = await Tasks.AddAsync(new NewTaskRequest
        {
            Title = title,
            DueAt = due,
            DueHasTime = dueHasTime,
            ParentTaskId = parentId,
            TagNames = tagNames ?? [],
            Status = status,
            Priority = priority,
            ProjectName = projectName,
            RemindOffsetMinutes = remindOffsetMinutes,
            Recurrence = recurrence,
        });
        return result.Created[0];
    }

    protected async Task<TaskItem> GetAsync(Guid id) => (await Tasks.GetAsync(id))!;

    public void Dispose()
    {
        Db.Dispose();
        GC.SuppressFinalize(this);
    }
}

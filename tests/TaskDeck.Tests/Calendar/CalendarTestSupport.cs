using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views.Calendar;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Calendar;

/// <summary>UI スレッドの代わりにその場で実行する（カレンダーのテスト用。名前は他の担当のものとぶつけない）。</summary>
internal sealed class CalendarImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>日付ごとに天気を与える。</summary>
internal sealed class CalendarFakeWeather : IWeatherProvider
{
    public Dictionary<DateOnly, WeatherDay> Days { get; } = [];

    public WeatherDay? Get(DateOnly date) => Days.GetValueOrDefault(date);
}

/// <summary>
/// カレンダーの ViewModel を、リポジトリを NSubstitute にして組み立てる。時計は JST 2026-09-22（火）10:00。
/// GetInDueRangeAsync は Items のうち範囲に入るものを返し、渡された範囲は RangeQueries に残る。
/// </summary>
internal sealed class CalendarFixture
{
    public static readonly DateTime Now = FixedClock.LocalToUtc(2026, 9, 22, 10, 0);

    private double _order;

    public CalendarFixture()
    {
        Undo = new UndoService(Tasks, Stack, NullLogger<UndoService>.Instance);
        Tasks.GetInDueRangeAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var from = call.ArgAt<DateTime>(0);
                var to = call.ArgAt<DateTime>(1);
                var includeClosed = call.ArgAt<bool>(2);
                RangeQueries.Add((from, to, includeClosed));
                return Task.FromResult<IReadOnlyList<TaskItem>>(
                    [.. Items.Where(t => t.DueAt >= from && t.DueAt < to && (includeClosed || t.IsOpen))]);
            });
        Tasks.GetOpenRecurringAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<RecurringTaskInfo>>([.. Recurring]));
        Projects.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Project>>([.. ProjectList]));
        Tasks.UpdateAsync(Arg.Any<Guid>(), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<Guid>(0);
                var mutate = call.ArgAt<Action<TaskItem>>(1);
                var before = Items.Single(t => t.Id == id);
                var after = before.Clone();
                mutate(after);
                Updates.Add(after);
                return Task.FromResult(new TaskMutationResult(new ChangeSet([new TaskSnapshot(before.Clone(), [])], [], []), [after], []));
            });
    }

    public FixedClock Clock { get; } = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    public ITaskRepository Tasks { get; } = Substitute.For<ITaskRepository>();

    public IProjectRepository Projects { get; } = Substitute.For<IProjectRepository>();

    public FakeSettingsStore Settings { get; } = new();

    public DataChangeHub Hub { get; } = new();

    public UndoStack Stack { get; } = new();

    public UndoService Undo { get; }

    public CalendarFakeWeather Weather { get; } = new();

    public List<TaskItem> Items { get; } = [];

    public List<RecurringTaskInfo> Recurring { get; } = [];

    public List<Project> ProjectList { get; } = [];

    public List<(DateTime From, DateTime To, bool IncludeClosed)> RangeQueries { get; } = [];

    /// <summary>UpdateAsync の mutate をかけた後の値（書いた順）。</summary>
    public List<TaskItem> Updates { get; } = [];

    public CalendarViewModel Create(IHolidayProvider? holidays = null)
    {
        var fixedHolidays = holidays ?? new FixedHolidays();
        return new CalendarViewModel(
            Tasks,
            Projects,
            new RecurrenceEngine(Clock, fixedHolidays, Settings),
            fixedHolidays,
            Weather,
            Settings,
            Clock,
            Undo,
            new ThemeService(Settings),
            Hub,
            new CalendarImmediateDispatcher(),
            NullLogger<CalendarViewModel>.Instance);
    }

    /// <summary>タスクを作って Items に入れる。dueLocal はローカル日時（hasTime=false なら日付だけ使う）。</summary>
    public TaskItem AddTask(
        string title,
        DateTime? dueLocal,
        bool hasTime = false,
        TaskItemStatus status = TaskItemStatus.NotStarted,
        int? durationMinutes = null,
        Priority priority = Priority.None,
        Guid? projectId = null)
    {
        var task = NewTask(title, dueLocal, hasTime, status, durationMinutes, priority, projectId);
        Items.Add(task);
        return task;
    }

    public TaskItem NewTask(
        string title,
        DateTime? dueLocal,
        bool hasTime = false,
        TaskItemStatus status = TaskItemStatus.NotStarted,
        int? durationMinutes = null,
        Priority priority = Priority.None,
        Guid? projectId = null)
    {
        DateTime? dueUtc = dueLocal is { } local
            ? hasTime
                ? FixedClock.LocalToUtc(local.Year, local.Month, local.Day, local.Hour, local.Minute)
                : FixedClock.LocalToUtc(local.Year, local.Month, local.Day)
            : null;
        return new TaskItem
        {
            Title = title,
            DueAt = dueUtc,
            DueHasTime = dueUtc is not null && hasTime,
            Status = status,
            CompletedAt = status is TaskItemStatus.Completed or TaskItemStatus.Cancelled ? Now : null,
            DurationMinutes = durationMinutes,
            Priority = priority,
            ProjectId = projectId,
            SortOrder = _order += SortOrderMath.Step,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
    }

    /// <summary>毎日などの繰り返しを付けて Recurring に入れる。</summary>
    public RecurringTaskInfo AddRecurring(TaskItem task, string rrule)
    {
        var rule = new RecurrenceRule { RRule = rrule, AnchorAt = task.DueAt ?? Now };
        task.RecurrenceRuleId = rule.Id;
        task.RecurrenceSeriesId = task.Id;
        var info = new RecurringTaskInfo(task, rule);
        Recurring.Add(info);
        return info;
    }

    public static DateOnly Day(int month, int day) => new(2026, month, day);

    /// <summary>その日のセル（月表示・週表示のどちらか出ている方）。</summary>
    public static CalendarDay DayOf(CalendarViewModel viewModel, DateOnly date) =>
        viewModel.MonthDays.Concat(viewModel.WeekDays).Single(d => d.Date == date);

    /// <summary>イベント経由（async void）で進む処理を待つ。2秒たっても満たされなければ false。</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(20);
        }
        return condition();
    }
}

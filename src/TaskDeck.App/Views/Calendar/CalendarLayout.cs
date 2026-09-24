using System.Globalization;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Calendar;

/// <summary>表示範囲と見出し。日付はローカルで数え、DB を引く境界だけ UTC に直す。</summary>
public static class CalendarRange
{
    /// <summary>月表示は6週（42日）に固定する（月によって行の高さが変わらないように）。</summary>
    public const int MonthDays = 42;

    public const int WeekDays = 7;

    public static DayOfWeek FirstDayOfWeek(bool weekStartsOnMonday) =>
        weekStartsOnMonday ? DayOfWeek.Monday : DayOfWeek.Sunday;

    /// <summary>date を含む週の初日。</summary>
    public static DateOnly WeekStart(DateOnly date, DayOfWeek firstDay) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)firstDay + 7) % 7));

    /// <summary>月表示の左上の日（その月の1日を含む週の初日）。</summary>
    public static DateOnly MonthGridStart(DateOnly anyDayInMonth, DayOfWeek firstDay) =>
        WeekStart(FirstOfMonth(anyDayInMonth), firstDay);

    public static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>ローカルの [firstDay, firstDay+days) を、期限（UTC）の範囲 [from, to) に直す。</summary>
    public static (DateTime FromUtc, DateTime ToUtc) ToUtc(DateOnly firstDay, int days, IClock clock) =>
        (clock.LocalDayStartUtc(firstDay), clock.LocalDayStartUtc(firstDay.AddDays(days)));

    /// <summary>「2026年9月」。</summary>
    public static string MonthTitle(DateOnly month) =>
        string.Create(CultureInfo.InvariantCulture, $"{month.Year}年{month.Month}月");

    /// <summary>「9月21日〜27日」。月や年をまたぐときは、またいだ側にも月（年）を書く。</summary>
    public static string WeekTitle(DateOnly start)
    {
        var end = start.AddDays(WeekDays - 1);
        if (start.Year != end.Year)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{start.Year}年{start.Month}月{start.Day}日〜{end.Year}年{end.Month}月{end.Day}日");
        }
        return start.Month != end.Month
            ? string.Create(CultureInfo.InvariantCulture, $"{start.Month}月{start.Day}日〜{end.Month}月{end.Day}日")
            : string.Create(CultureInfo.InvariantCulture, $"{start.Month}月{start.Day}日〜{end.Day}日");
    }
}

/// <summary>読み込んだものを日ごとに組み立てるための材料。ProjectColors はテーマ変換前（ライト）の色。</summary>
public sealed record CalendarSource(
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<RecurringTaskInfo> Recurring,
    IReadOnlyDictionary<Guid, string> ProjectColors,
    bool IsDark);

/// <summary>
/// 表示範囲の日を組み立てる（UI に依存しない。スレッドプールで走らせてよい）。
/// 期限はローカル日付でその日に入れる（深夜 0:30 の期限は、UTC では前日でも、ローカルの当日に入る）。
/// </summary>
public static class CalendarBuilder
{
    public const int MinutesPerDay = 24 * 60;

    public static IReadOnlyList<CalendarDay> Build(
        CalendarMode mode,
        DateOnly firstDay,
        int dayCount,
        CalendarSource source,
        IRecurrenceEngine recurrence,
        IHolidayProvider holidays,
        IWeatherProvider weather,
        IClock clock,
        DateOnly? month = null,
        int maxVisible = CalendarDay.MaxVisibleEntries)
    {
        var endDay = firstDay.AddDays(dayCount);
        var byDay = new Dictionary<DateOnly, List<CalendarEntry>>();
        void Add(CalendarEntry entry)
        {
            if (entry.Date < firstDay || entry.Date >= endDay)
            {
                return;
            }
            if (!byDay.TryGetValue(entry.Date, out var list))
            {
                list = [];
                byDay[entry.Date] = list;
            }
            list.Add(entry);
        }

        foreach (var task in source.Tasks)
        {
            if (task.DueAt is { } due)
            {
                Add(CreateEntry(task, due, task.DueHasTime, isGhost: false, source, clock));
            }
        }

        // 繰り返しの仮表示（F-064）: いま開いている回より後で、表示範囲にある回。期限の無い繰り返しはカレンダーに載らない
        var (fromUtc, toUtc) = CalendarRange.ToUtc(firstDay, dayCount, clock);
        foreach (var info in source.Recurring)
        {
            if (info.Task.DueAt is not { } due)
            {
                continue;
            }
            foreach (var occurrence in recurrence.Occurrences(info.Rule, due, info.Task.DueHasTime, fromUtc, toUtc))
            {
                Add(CreateEntry(info.Task, occurrence, info.Task.DueHasTime, isGhost: true, source, clock));
            }
        }

        var today = clock.LocalToday();
        var days = new List<CalendarDay>(dayCount);
        for (var i = 0; i < dayCount; i++)
        {
            var date = firstDay.AddDays(i);
            var all = byDay.TryGetValue(date, out var list) ? Sort(list) : [];
            // 週表示: 時刻ありは時間軸へ、日付のみは終日帯へ
            var entries = mode == CalendarMode.Week ? [.. all.Where(e => !e.HasTime)] : all;
            var blocks = mode == CalendarMode.Week ? LayoutDay(all.Where(e => e.HasTime)) : [];
            days.Add(new CalendarDay
            {
                Date = date,
                IsToday = date == today,
                IsOtherMonth = month is { } m && (date.Year != m.Year || date.Month != m.Month),
                HolidayName = holidays.GetName(date),
                Weather = WeatherLook.From(weather.Get(date)),
                Entries = entries,
                VisibleEntries = entries.Count > maxVisible ? [.. entries.Take(maxVisible)] : entries,
                Blocks = blocks,
            });
        }
        return days;
    }

    /// <summary>
    /// 1日の中の並び: 未完了 → 繰り返しの仮表示 → 完了・中止。その中は終日 → 時刻順 → 優先度の高い順 → 手動の並び順。
    /// 「3件＋他 N 件」で先に見せたいものが前に来る。
    /// </summary>
    public static List<CalendarEntry> Sort(IEnumerable<CalendarEntry> entries) =>
    [
        .. entries
            .OrderBy(e => e.IsClosed ? 2 : e.IsGhost ? 1 : 0)
            .ThenBy(e => e.HasTime)
            .ThenBy(e => e.Time)
            .ThenByDescending(e => e.Task.Priority)
            .ThenBy(e => e.Task.SortOrder)
            .ThenBy(e => e.Task.Id),
    ];

    /// <summary>
    /// 時刻ありのものを時間軸に並べる。重なるもの（[開始, 終了) が交わるもの）の塊ごとに、空いている一番左の列へ入れ、
    /// 塊の列の数で幅を割る。長さは所要時間（無ければ30分）、見た目は20分より短くしない。
    /// </summary>
    public static IReadOnlyList<WeekBlock> LayoutDay(IEnumerable<CalendarEntry> timed)
    {
        var items = timed
            .Select(e => (Entry: e, Start: e.StartMinute, End: Math.Min(MinutesPerDay, e.StartMinute + Math.Max(e.DurationMinutes, WeekBlock.MinVisualMinutes))))
            .OrderBy(x => x.Start)
            .ThenByDescending(x => x.End)
            .ToList();
        var result = new List<WeekBlock>(items.Count);
        var cluster = new List<(CalendarEntry Entry, int Start, int End, int Column)>();
        var columnEnds = new List<int>();
        var clusterEnd = 0;
        foreach (var item in items)
        {
            if (cluster.Count > 0 && item.Start >= clusterEnd)
            {
                Flush();
            }
            var column = columnEnds.FindIndex(end => end <= item.Start);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(item.End);
            }
            else
            {
                columnEnds[column] = item.End;
            }
            cluster.Add((item.Entry, item.Start, item.End, column));
            clusterEnd = Math.Max(clusterEnd, item.End);
        }
        Flush();
        return result;

        void Flush()
        {
            foreach (var c in cluster)
            {
                result.Add(new WeekBlock { Entry = c.Entry, StartMinute = c.Start, EndMinute = c.End, Column = c.Column, ColumnCount = columnEnds.Count });
            }
            cluster.Clear();
            columnEnds.Clear();
            clusterEnd = 0;
        }
    }

    private static CalendarEntry CreateEntry(TaskItem task, DateTime dueUtc, bool hasTime, bool isGhost, CalendarSource source, IClock clock)
    {
        var local = clock.ToLocal(dueUtc);
        var color = task.ProjectId is { } projectId && source.ProjectColors.TryGetValue(projectId, out var hex)
            ? ProjectPalette.ForTheme(hex, source.IsDark)
            : null;
        return new CalendarEntry
        {
            Task = task,
            Date = DateOnly.FromDateTime(local),
            Time = hasTime ? TimeOnly.FromDateTime(local) : null,
            IsGhost = isGhost,
            IsOverdue = !isGhost && TaskRules.IsOverdue(task, clock),
            ColorHex = color,
            TintHex = color is { Length: 7 } ? (source.IsDark ? "#2E" : "#14") + color[1..] : null,
            AutomationName = isGhost ? GhostName(task.Title, local, hasTime) : DisplayText.RowAutomationName(task, clock),
        };
    }

    private static string GhostName(string title, DateTime local, bool hasTime) =>
        string.Create(CultureInfo.InvariantCulture, $"繰り返し予定、{title}、{local.Month}月{local.Day}日{(hasTime ? local.ToString(" H:mm", CultureInfo.InvariantCulture) : "")}");
}

/// <summary>ドラッグで変える期限（DueAt は UTC）。</summary>
public readonly record struct DueChange(DateTime DueAt, bool DueHasTime, int? DurationMinutes);

/// <summary>ドラッグの結果の計算（F-063）。時刻は15分単位にそろえる。</summary>
public static class CalendarDrag
{
    public const int SnapMinutes = 15;

    /// <summary>分を15分単位に丸め、[0, max] に収める。</summary>
    public static int Snap(double minutes, int max = CalendarBuilder.MinutesPerDay - SnapMinutes) =>
        Math.Clamp((int)Math.Round(minutes / SnapMinutes, MidpointRounding.AwayFromZero) * SnapMinutes, 0, max);

    /// <summary>月表示で別の日へ: 時刻ありは時刻を保ち、日付のみは日付のみのまま。</summary>
    public static DueChange ToDate(TaskItem task, DateOnly date, IClock clock) =>
        task.DueHasTime && task.DueAt is { } due
            ? new DueChange(clock.LocalToUtc(date, TimeOnly.FromDateTime(clock.ToLocal(due))), true, task.DurationMinutes)
            : new DueChange(TaskRules.DateOnlyDue(date, clock), false, task.DurationMinutes);

    /// <summary>週表示の終日帯へ: 日付のみにする。</summary>
    public static DueChange ToAllDay(TaskItem task, DateOnly date, IClock clock) =>
        new(TaskRules.DateOnlyDue(date, clock), false, task.DurationMinutes);

    /// <summary>週表示の時間軸へ: その日のその時刻（15分単位）にする。日付のみだったものは時刻ありになる。</summary>
    public static DueChange ToTime(TaskItem task, DateOnly date, double startMinute, IClock clock) =>
        new(clock.LocalToUtc(date, TimeAt(Snap(startMinute))), true, task.DurationMinutes);

    /// <summary>上端を動かす: 終わりはそのままで、始まりと所要時間が変わる（15分より短くしない）。</summary>
    public static DueChange ResizeTop(TaskItem task, double newStartMinute, IClock clock)
    {
        var (date, start) = LocalStart(task, clock);
        var end = start + DurationOf(task);
        var newStart = Math.Min(Snap(newStartMinute), Math.Max(0, end - SnapMinutes));
        return new DueChange(clock.LocalToUtc(date, TimeAt(newStart)), true, end - newStart);
    }

    /// <summary>下端を動かす: 始まりはそのままで、所要時間だけが変わる（15分より短くしない）。</summary>
    public static DueChange ResizeBottom(TaskItem task, double newEndMinute, IClock clock)
    {
        var (_, start) = LocalStart(task, clock);
        var newEnd = Math.Max(Snap(newEndMinute, CalendarBuilder.MinutesPerDay), start + SnapMinutes);
        return new DueChange(task.DueAt ?? throw new InvalidOperationException("期限の無いタスクは所要時間を変えられません。"), true, newEnd - start);
    }

    private static int DurationOf(TaskItem task) =>
        task.DurationMinutes is { } m && m > 0 ? m : CalendarEntry.DefaultDurationMinutes;

    private static (DateOnly Date, int Minute) LocalStart(TaskItem task, IClock clock)
    {
        var local = clock.ToLocal(task.DueAt ?? throw new InvalidOperationException("期限の無いタスクは所要時間を変えられません。"));
        return (DateOnly.FromDateTime(local), (local.Hour * 60) + local.Minute);
    }

    private static TimeOnly TimeAt(int minute) => new(minute / 60, minute % 60);
}

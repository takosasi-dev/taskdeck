using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Calendar;

/// <summary>読み込みが画面に入った（View が描画まで含めた時間を測るため）。</summary>
public sealed class CalendarReloadedEventArgs(Stopwatch watch) : EventArgs
{
    public Stopwatch Watch { get; } = watch;
}

/// <summary>
/// カレンダー（S-04、F-061〜F-065）。表示範囲の期限だけを読み、日ごとに並べる。ドラッグの結果は期限・所要時間の変更として書き、取り消せるように積む。
/// 画面に出ていない間は読み込まない（データが変わったら印だけ付け、出たときに読み直す）。View の型は持たない。
/// </summary>
public sealed partial class CalendarViewModel : ObservableObject
{
    /// <summary>変更通知をまとめる時間。</summary>
    public const int ChangeDebounceMs = 50;

    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IRecurrenceEngine _recurrence;
    private readonly IHolidayProvider _holidays;
    private readonly IWeatherProvider _weather;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly UndoService _undo;
    private readonly ThemeService _theme;
    private readonly ILogger<CalendarViewModel> _logger;
    private readonly Debouncer _reloadDebouncer;

    private IReadOnlyDictionary<Guid, string>? _projectColors;
    private bool _isActive;
    private bool _isDirty = true;
    private int _loadVersion;
    private DateOnly _loadedToday;
    private bool _loadedWeekStartsOnMonday;
    private int _monthChipCapacity = CalendarDay.MaxVisibleEntries;

    public CalendarViewModel(
        ITaskRepository tasks,
        IProjectRepository projects,
        IRecurrenceEngine recurrence,
        IHolidayProvider holidays,
        IWeatherProvider weather,
        ISettingsStore settings,
        IClock clock,
        UndoService undo,
        ThemeService theme,
        DataChangeHub hub,
        IUiDispatcher dispatcher,
        ILogger<CalendarViewModel> logger)
    {
        _tasks = tasks;
        _projects = projects;
        _recurrence = recurrence;
        _holidays = holidays;
        _weather = weather;
        _settings = settings;
        _clock = clock;
        _undo = undo;
        _theme = theme;
        _logger = logger;
        _anchor = clock.LocalToday();
        _reloadDebouncer = new Debouncer(ReloadAsync);

        // アプリと同じ寿命（シングルトン）なので外さない
        hub.Changed += (_, e) => dispatcher.Post(() => OnDataChanged(e.Kinds));
        settings.Changed += (_, _) => dispatcher.Post(OnSettingsChanged);
        theme.ThemeChanged += (_, _) => dispatcher.Post(() => Invalidate());
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonth), nameof(IsWeek), nameof(PreviousLabel), nameof(NextLabel))]
    private CalendarMode _mode = CalendarMode.Month;

    /// <summary>表示の基準日（月表示はこの日の月、週表示はこの日を含む週）。</summary>
    [ObservableProperty]
    private DateOnly _anchor;

    /// <summary>「2026年9月」「9月21日〜27日」。</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>月表示の曜日の見出し（週の開始曜日から7つ）。</summary>
    [ObservableProperty]
    private IReadOnlyList<CalendarWeekday> _weekdays = [];

    /// <summary>月表示の42日（週表示の間は空）。</summary>
    [ObservableProperty]
    private IReadOnlyList<CalendarDay> _monthDays = [];

    /// <summary>週表示の7日（月表示の間は空）。</summary>
    [ObservableProperty]
    private IReadOnlyList<CalendarDay> _weekDays = [];

    /// <summary>週表示で今日が入っている（現在時刻の線を出す）。</summary>
    [ObservableProperty]
    private bool _isTodayInWeek;

    /// <summary>ローカルの現在時刻（0:00 からの分）。</summary>
    [ObservableProperty]
    private double _nowMinute;

    /// <summary>選んでいるタスク（View の SelectedTaskId と双方向。メイン画面が詳細ペインに出す）。</summary>
    [ObservableProperty]
    private Guid? _selectedTaskId;

    /// <summary>月表示のセルの枠（42個。作ったまま使い回す）。</summary>
    public IReadOnlyList<CalendarCell> MonthCells { get; } = [.. Enumerable.Range(0, CalendarRange.MonthDays).Select(_ => new CalendarCell())];

    /// <summary>週表示の列の枠（7個。作ったまま使い回す）。</summary>
    public IReadOnlyList<CalendarCell> WeekCells { get; } = [.. Enumerable.Range(0, CalendarRange.WeekDays).Select(_ => new CalendarCell())];

    public bool IsMonth => Mode == CalendarMode.Month;

    public bool IsWeek => Mode == CalendarMode.Week;

    public string PreviousLabel => IsMonth ? "前の月" : "前の週";

    public string NextLabel => IsMonth ? "次の月" : "次の週";

    /// <summary>読み込んだ内容が画面に入った。</summary>
    public event EventHandler<CalendarReloadedEventArgs>? Reloaded;

    /// <summary>画面に出た。前に出したときから何か変わっていれば読み直す。</summary>
    public Task ActivateAsync()
    {
        _isActive = true;
        return _isDirty || _loadedToday != _clock.LocalToday() ? ReloadAsync() : Task.CompletedTask;
    }

    /// <summary>画面から外れた（データが変わっても読まない）。</summary>
    public void Deactivate() => _isActive = false;

    /// <summary>
    /// 月表示のセルに入るチップの数（View がセルの高さから入れる。上限3、下限1）。窓が低くて3件入らないときに減らし、
    /// 入らない分は「他 N 件」に数える（読み直さず、読み込んだものの見せ方だけを変える）。
    /// </summary>
    public void SetMonthChipCapacity(int count)
    {
        count = Math.Clamp(count, 1, CalendarDay.MaxVisibleEntries);
        if (count == _monthChipCapacity)
        {
            return;
        }
        _monthChipCapacity = count;
        if (MonthDays.Count == 0)
        {
            return;
        }
        MonthDays = [.. MonthDays.Select(d => d.WithMaxVisible(count))];
        for (var i = 0; i < MonthCells.Count; i++)
        {
            MonthCells[i].Day = MonthDays[i];
        }
    }

    /// <summary>1分ごと（View のタイマー）。現在時刻の線を動かし、日付が変わっていたら読み直す（今日の強調を移す）。</summary>
    public void OnMinuteTick()
    {
        NowMinute = _clock.LocalNow().TimeOfDay.TotalMinutes;
        if (_isActive && _loadedToday != _clock.LocalToday())
        {
            Invalidate();
        }
    }

    /// <summary>表示範囲を読み直す。速く切り替えたときは最後の1回だけを画面に入れる。</summary>
    public async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        var watch = Stopwatch.StartNew();
        _isDirty = false;

        var mode = Mode;
        var weekStartsOnMonday = _settings.Current.Calendar.WeekStartsOnMonday;
        var firstDayOfWeek = CalendarRange.FirstDayOfWeek(weekStartsOnMonday);
        var (first, count) = mode == CalendarMode.Month
            ? (CalendarRange.MonthGridStart(Anchor, firstDayOfWeek), CalendarRange.MonthDays)
            : (CalendarRange.WeekStart(Anchor, firstDayOfWeek), CalendarRange.WeekDays);
        var (fromUtc, toUtc) = CalendarRange.ToUtc(first, count, _clock);

        // 見出しは先に変える（押した手応えを待たせない）
        Title = mode == CalendarMode.Month ? CalendarRange.MonthTitle(Anchor) : CalendarRange.WeekTitle(first);
        if (Weekdays.Count == 0 || Weekdays[0].Day != firstDayOfWeek)
        {
            Weekdays = [.. Enumerable.Range(0, 7).Select(i => new CalendarWeekday((DayOfWeek)(((int)firstDayOfWeek + i) % 7)))];
        }

        var tasksTask = _tasks.GetInDueRangeAsync(fromUtc, toUtc, includeClosed: true);
        var recurring = await _tasks.GetOpenRecurringAsync();
        var colors = _projectColors ?? await LoadProjectColorsAsync();
        var tasks = await tasksTask;
        if (version != _loadVersion)
        {
            return;
        }
        var source = new CalendarSource(tasks, recurring, colors, _theme.IsDark);
        DateOnly? month = mode == CalendarMode.Month ? Anchor : null;
        var maxVisible = mode == CalendarMode.Month ? _monthChipCapacity : CalendarDay.MaxVisibleEntries;
        var days = await Task.Run(() => CalendarBuilder.Build(mode, first, count, source, _recurrence, _holidays, _weather, _clock, month, maxVisible));
        if (version != _loadVersion)
        {
            return;
        }

        var today = _clock.LocalToday();
        MonthDays = mode == CalendarMode.Month ? days : [];
        WeekDays = mode == CalendarMode.Week ? days : [];
        var cells = mode == CalendarMode.Month ? MonthCells : WeekCells;
        for (var i = 0; i < cells.Count; i++)
        {
            cells[i].Day = days[i];
        }
        IsTodayInWeek = mode == CalendarMode.Week && today >= first && today < first.AddDays(count);
        NowMinute = _clock.LocalNow().TimeOfDay.TotalMinutes;
        _loadedToday = today;
        _loadedWeekStartsOnMonday = weekStartsOnMonday;
        ApplySelection();

        _logger.LogDebug(
            "カレンダーを読み込みました（{Mode} {From}〜{To}、タスク {Tasks} 件・繰り返し予定の元 {Recurring} 件、{Elapsed} ms）",
            mode, first, first.AddDays(count - 1), tasks.Count, recurring.Count, watch.ElapsedMilliseconds);
        Reloaded?.Invoke(this, new CalendarReloadedEventArgs(watch));
    }

    // ---- ドラッグの結果（F-063）。仮表示と完了・中止は動かさない ----

    /// <summary>月表示で別の日へ（時刻は保つ）。</summary>
    public Task MoveToDateAsync(CalendarEntry entry, DateOnly date) =>
        MoveAsync(entry, CalendarDrag.ToDate(entry.Task, date, _clock));

    /// <summary>週表示の終日帯へ（日付のみにする）。</summary>
    public Task MoveToAllDayAsync(CalendarEntry entry, DateOnly date) =>
        MoveAsync(entry, CalendarDrag.ToAllDay(entry.Task, date, _clock));

    /// <summary>週表示の時間軸へ（15分単位。日付のみだったものは時刻ありになる）。</summary>
    public Task MoveToTimeAsync(CalendarEntry entry, DateOnly date, double startMinute) =>
        MoveAsync(entry, CalendarDrag.ToTime(entry.Task, date, startMinute, _clock));

    /// <summary>ブロックの上端（始まりが変わり、終わりは保つ）。</summary>
    public Task ResizeTopAsync(CalendarEntry entry, double startMinute) =>
        entry.HasTime ? ResizeAsync(entry, CalendarDrag.ResizeTop(entry.Task, startMinute, _clock)) : Task.CompletedTask;

    /// <summary>ブロックの下端（所要時間だけが変わる）。</summary>
    public Task ResizeBottomAsync(CalendarEntry entry, double endMinute) =>
        entry.HasTime ? ResizeAsync(entry, CalendarDrag.ResizeBottom(entry.Task, endMinute, _clock)) : Task.CompletedTask;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PreviousAsync() => NavigateAsync(-1);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task NextAsync() => NavigateAsync(1);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task TodayAsync()
    {
        Anchor = _clock.LocalToday();
        return ReloadAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ShowMonthAsync()
    {
        if (IsMonth)
        {
            return Task.CompletedTask;
        }
        // 週の真ん中の日の月にする（月をまたぐ週でも、多く入っている方の月になる）
        var firstDay = CalendarRange.FirstDayOfWeek(_settings.Current.Calendar.WeekStartsOnMonday);
        Anchor = CalendarRange.WeekStart(Anchor, firstDay).AddDays(3);
        Mode = CalendarMode.Month;
        return ReloadAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ShowWeekAsync()
    {
        if (IsWeek)
        {
            return Task.CompletedTask;
        }
        // 表示中の月に今日があれば今日の週、無ければその月の最初の週
        var today = _clock.LocalToday();
        Anchor = today.Year == Anchor.Year && today.Month == Anchor.Month ? today : CalendarRange.FirstOfMonth(Anchor);
        Mode = CalendarMode.Week;
        return ReloadAsync();
    }

    /// <summary>チップ・ブロックを押した（仮表示は元のタスクを選ぶ）。</summary>
    [RelayCommand]
    private void Select(CalendarEntry? entry)
    {
        if (entry is not null)
        {
            SelectedTaskId = entry.TaskId;
        }
    }

    partial void OnSelectedTaskIdChanged(Guid? value) => ApplySelection();

    private Task NavigateAsync(int step)
    {
        Anchor = IsMonth
            ? CalendarRange.FirstOfMonth(Anchor).AddMonths(step)
            : CalendarRange.WeekStart(Anchor, CalendarRange.FirstDayOfWeek(_settings.Current.Calendar.WeekStartsOnMonday)).AddDays(7 * step);
        return ReloadAsync();
    }

    private void ApplySelection()
    {
        foreach (var entry in MonthDays.Concat(WeekDays).SelectMany(d => d.AllEntries))
        {
            entry.IsSelected = entry.TaskId == SelectedTaskId;
        }
    }

    private async Task MoveAsync(CalendarEntry entry, DueChange change)
    {
        var label = DisplayText.Quote(entry.Title) + "の期限を" + DisplayText.DueShort(change.DueAt, change.DueHasTime, _clock) + "に移しました";
        await ApplyAsync(entry, change, label);
    }

    private async Task ResizeAsync(CalendarEntry entry, DueChange change)
    {
        var label = DisplayText.Quote(entry.Title) + "の所要時間を" + DisplayText.DurationName(change.DurationMinutes) + "にしました";
        await ApplyAsync(entry, change, label);
    }

    /// <summary>期限・所要時間だけを書き、取り消しに積む（トーストは出さない。Ctrl+Z で戻せる）。</summary>
    private async Task ApplyAsync(CalendarEntry entry, DueChange change, string label)
    {
        var task = entry.Task;
        if (!entry.CanDrag
            || (task.DueAt == change.DueAt && task.DueHasTime == change.DueHasTime && task.DurationMinutes == change.DurationMinutes))
        {
            return;
        }
        var result = await _tasks.UpdateAsync(task.Id, t =>
        {
            t.DueAt = change.DueAt;
            t.DueHasTime = change.DueHasTime;
            t.DurationMinutes = change.DurationMinutes;
        });
        _undo.Record(result, label, UndoKind.Other, showToast: false);
        _logger.LogDebug("カレンダーで期限を変えました: {TaskId}", task.Id);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> LoadProjectColorsAsync()
    {
        var projects = await _projects.GetAllAsync(includeArchived: true);
        var colors = projects.ToDictionary(p => p.Id, p => p.ColorHex);
        _projectColors = colors;
        return colors;
    }

    private void OnDataChanged(DataChangeKind kinds)
    {
        if ((kinds & (DataChangeKind.Tasks | DataChangeKind.Projects | DataChangeKind.ExternalCache)) == 0)
        {
            return;
        }
        Invalidate(projectsChanged: (kinds & DataChangeKind.Projects) != 0);
    }

    /// <summary>週の開始曜日が変わったときだけ並べ直す（設定の保存はウィンドウ配置などでも来る）。</summary>
    private void OnSettingsChanged()
    {
        if (_settings.Current.Calendar.WeekStartsOnMonday != _loadedWeekStartsOnMonday)
        {
            Invalidate();
        }
    }

    /// <summary>読み直しの印を付け、画面に出ていれば 50ms まとめてから読み直す。</summary>
    private void Invalidate(bool projectsChanged = false)
    {
        if (projectsChanged)
        {
            _projectColors = null;
        }
        _isDirty = true;
        if (_isActive)
        {
            _reloadDebouncer.Schedule(ChangeDebounceMs);
        }
    }
}

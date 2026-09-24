using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Stats;

/// <summary>
/// 振り返り（S-13、F-161〜F-16A）。担当: 波3-K。
/// 見えている間だけ読む: 表示されたら全期間の完了（GetCompletedFactsAsync(null)）とプロジェクトを読み、期間を切り替えるときは
/// 読み直さずに StatsCalculator で数え直す。タスク・プロジェクトが変わったら（DataChangeHub）、見えていれば少し待ってから読み直し、
/// 見えていなければ次に表示したときに読み直す。週の始まりの設定が変わったら数え直す。
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private const int ReloadDelayMs = 300;

    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly StatsCalculator _calculator;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<StatsViewModel> _logger;
    private readonly Debouncer _reload;

    private IReadOnlyList<CompletedTaskFact>? _facts;
    private IReadOnlyList<Project> _projectList = [];
    private bool _isActive;
    private bool _isDirty = true;
    private bool _isDark;
    private bool _weekStartsOnMonday;

    public StatsViewModel(
        ITaskRepository tasks,
        IProjectRepository projects,
        StatsCalculator calculator,
        ISettingsStore settings,
        DataChangeHub hub,
        IClock clock,
        IUiDispatcher ui,
        StatsExportViewModel export,
        ILogger<StatsViewModel> logger)
    {
        _tasks = tasks;
        _projects = projects;
        _calculator = calculator;
        _settings = settings;
        _clock = clock;
        _ui = ui;
        _logger = logger;
        Export = export;
        _weekStartsOnMonday = settings.Current.Calendar.WeekStartsOnMonday;
        _reload = new Debouncer(ReloadAsync);
        // アプリと同じ寿命（メイン画面の中の1枚）なので外さない
        hub.Changed += OnDataChanged;
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>Markdown で書き出す（F-108・F-109）。</summary>
    public StatsExportViewModel Export { get; }

    /// <summary>今週／今月／今年（F-161）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeek), nameof(IsMonth), nameof(IsYear))]
    private StatsPeriod _period = StatsPeriod.ThisWeek;

    /// <summary>「表で見る」（F-169）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TableButtonText))]
    private bool _isTableMode;

    /// <summary>画面に出すもの。まだ読んでいなければ null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded))]
    private StatsDisplay? _display;

    public bool IsLoaded => Display is not null;

    public bool IsWeek
    {
        get => Period == StatsPeriod.ThisWeek;
        set => SelectPeriodIf(value, StatsPeriod.ThisWeek);
    }

    public bool IsMonth
    {
        get => Period == StatsPeriod.ThisMonth;
        set => SelectPeriodIf(value, StatsPeriod.ThisMonth);
    }

    public bool IsYear
    {
        get => Period == StatsPeriod.ThisYear;
        set => SelectPeriodIf(value, StatsPeriod.ThisYear);
    }

    public string TableButtonText => IsTableMode ? "グラフで見る" : "表で見る";

    /// <summary>画面に出た／隠れた（メイン画面で振り返りを選んだとき）。出たときに古ければ読み直す。</summary>
    public async Task SetActiveAsync(bool active)
    {
        _isActive = active;
        if (active && _isDirty)
        {
            await ReloadAsync();
        }
    }

    /// <summary>テーマが変わった（プロジェクト色のダーク用の値に切り替える）。</summary>
    public void SetDarkTheme(bool isDark)
    {
        if (_isDark == isDark)
        {
            return;
        }
        _isDark = isDark;
        Recompute();
    }

    /// <summary>全期間の完了とプロジェクトを読み直して数える（読み込みはリポジトリがスレッドプールで行う）。</summary>
    public async Task ReloadAsync()
    {
        _isDirty = false;
        var started = Environment.TickCount64;
        var facts = await _tasks.GetCompletedFactsAsync(null);
        _projectList = await _projects.GetAllAsync(includeArchived: true);
        _facts = facts;
        Recompute();
        Export.OnPeriodShown(Period);
        _logger.LogDebug("振り返りを集計しました（完了 {Count} 件、{Milliseconds} ms）", facts.Count, Environment.TickCount64 - started);
    }

    [RelayCommand]
    private void ToggleTable() => IsTableMode = !IsTableMode;

    partial void OnPeriodChanged(StatsPeriod value)
    {
        Recompute();
        Export.OnPeriodShown(value);
    }

    private void SelectPeriodIf(bool selected, StatsPeriod period)
    {
        if (selected)
        {
            Period = period;
        }
    }

    private void Recompute()
    {
        if (_facts is null)
        {
            return;
        }
        var report = _calculator.Calculate(_facts, Period, _projectList, _weekStartsOnMonday);
        Display = StatsPresenter.Present(report, _clock.LocalToday(), _weekStartsOnMonday, _isDark);
    }

    /// <summary>発火はどのスレッドからも来る。UI に戻してから、見えていれば少し待って読み直す。</summary>
    private void OnDataChanged(object? sender, DataChangedEventArgs e)
    {
        if ((e.Kinds & (DataChangeKind.Tasks | DataChangeKind.Projects)) == 0)
        {
            return;
        }
        _ui.Post(() =>
        {
            _isDirty = true;
            if (_isActive)
            {
                _reload.Schedule(ReloadDelayMs);
            }
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var monday = _settings.Current.Calendar.WeekStartsOnMonday;
        _ui.Post(() =>
        {
            if (monday == _weekStartsOnMonday)
            {
                return;
            }
            _weekStartsOnMonday = monday;
            Recompute();
            Export.OnPeriodShown(Period);
        });
    }
}

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Data.External;

namespace TaskDeck.App.BackgroundTasks;

/// <summary>
/// 外部データの取得を裏で回す（設計書 4.7.8「すべて IHostedService で。UI から直接呼ばない」）。担当: 波3-K。
/// 起動の15秒後に1回、その後は1時間ごとに見て、期限の来たものだけ取る（どれも実質1日1回まで）:
/// 祝日（今年・来年がまだなら）・天気（起動時は前回から6時間、動いている間は前回から1日。設計書 4.7.4）・
/// 更新の確認（前回から1日。リポジトリ名が null の間は通信しない）。見る間隔を1時間にしているのは、スリープから戻ったときに
/// 遅れを取り戻すため（期限は保存した取得日時で判断する）。
/// 設定が変わったら（オフラインを解除・各機能を ON・天気の場所を変えた）待たずに回す。祝日・天気の表示の ON/OFF と場所の変更は、
/// すぐ ExternalCache を出してカレンダーに読み直させる（HolidayCache・WeatherCache は設定を見て答えを変える）。
/// 失敗は静かに（ログは Warning まで。画面には出さない）。
/// </summary>
internal sealed class ExternalDataWorker : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>動いている間の天気の取り直しの間隔（起動時は WeatherUpdater.MaxAge の6時間）。</summary>
    public static readonly TimeSpan RunningWeatherMaxAge = TimeSpan.FromDays(1);

    private readonly HolidayUpdater _holidays;
    private readonly WeatherUpdater _weather;
    private readonly UpdateChecker _updates;
    private readonly ISettingsStore _settings;
    private readonly DataChangeHub _hub;
    private readonly ILogger<ExternalDataWorker> _logger;
    private readonly Channel<bool> _wake = Channel.CreateUnbounded<bool>();
    private Watched _watched;

    public ExternalDataWorker(
        HolidayUpdater holidays,
        WeatherUpdater weather,
        UpdateChecker updates,
        ISettingsStore settings,
        DataChangeHub hub,
        ILogger<ExternalDataWorker> logger)
    {
        _holidays = holidays;
        _weather = weather;
        _updates = updates;
        _settings = settings;
        _hub = hub;
        _logger = logger;
        _watched = Watched.From(settings.Current.External);
        settings.Changed += OnSettingsChanged;
    }

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);
        var weatherMaxAge = WeatherUpdater.MaxAge;
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(weatherMaxAge, stoppingToken);
            weatherMaxAge = RunningWeatherMaxAge;
            await WaitAsync(stoppingToken);
        }
    }

    /// <summary>期限の来たものを取る。オフラインなら何もしない。</summary>
    private async Task RunOnceAsync(TimeSpan weatherMaxAge, CancellationToken ct)
    {
        if (_settings.Current.External.OfflineMode)
        {
            return;
        }
#pragma warning disable CA1031 // 裏で回す取得の最上位: DB やファイルの失敗もログに残して続ける（次の回でまた試す。App.xaml.cs の保守と同じ扱い）
        try
        {
            await _holidays.RefreshAsync(force: false, ct);
            await _weather.RefreshAsync(force: false, ct, weatherMaxAge);
            await _updates.CheckAsync(force: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "外部データの取得に失敗しました");
        }
#pragma warning restore CA1031
    }

    /// <summary>1時間たつか、設定が変わって起こされるまで待つ（起こされた分はまとめて1回にする）。</summary>
    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timer.CancelAfter(Interval);
        try
        {
            await _wake.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // 1時間たった（起こされずに回る）
        }
        while (_wake.Reader.TryRead(out _))
        {
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var now = Watched.From(_settings.Current.External);
        var before = Interlocked.Exchange(ref _watched, now);
        if (now == before)
        {
            return;
        }
        if (now.HolidaysEnabled != before.HolidaysEnabled || now.WeatherEnabled != before.WeatherEnabled
            || now.WeatherLocationKey != before.WeatherLocationKey)
        {
            // 祝日・天気を出す／出さないが変わった。カレンダーなどに読み直させる
            _hub.Publish(DataChangeKind.ExternalCache);
        }
        var wake = (before.OfflineMode && !now.OfflineMode)
            || (!before.HolidaysEnabled && now.HolidaysEnabled)
            || (!before.WeatherEnabled && now.WeatherEnabled)
            || (!before.UpdateCheckEnabled && now.UpdateCheckEnabled)
            || now.WeatherLocationKey != before.WeatherLocationKey;
        if (wake)
        {
            _wake.Writer.TryWrite(true);
        }
    }

    /// <summary>取得に関わる設定の写し（変わったかを比べる）。</summary>
    private sealed record Watched(bool OfflineMode, bool HolidaysEnabled, bool WeatherEnabled, string? WeatherLocationKey, bool UpdateCheckEnabled)
    {
        public static Watched From(ExternalSettings s) => new(
            s.OfflineMode,
            s.HolidaysEnabled,
            s.WeatherEnabled,
            s.WeatherLocation is { } location ? WeatherUpdater.LocationKey(location) : null,
            s.UpdateCheckEnabled);
    }
}

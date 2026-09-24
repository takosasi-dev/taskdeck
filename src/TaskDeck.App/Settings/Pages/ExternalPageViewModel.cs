using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;
using TaskDeck.Data.External;

namespace TaskDeck.App.Settings.Pages;

/// <summary>
/// 外部サービス（F-191〜F-199）。担当: 波1-D が値の保存まで、波3-K が効かせる部分（天気の場所・今すぐ取得・取得の状況）。
/// 外部API はすべて任意機能で、落ちてもアプリは完全に動く（CLAUDE.md 1.6）。
/// 天気の場所は地名で探して選ぶ（送るのは入力した地名だけ。選んだ場所の緯度経度は小数第2位に丸めて保存する）。
/// 自動の取得に失敗しても何も出さないが、「今すぐ取得」「探す」は押した結果をこのページに1行で出す。
/// </summary>
public sealed partial class ExternalPageViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly Services? _services;

    /// <summary>値の保存だけ（通信の部品なし。取得の状況は出さない）。</summary>
    public ExternalPageViewModel(ISettingsStore settings)
    {
        _settings = settings;
    }

    public ExternalPageViewModel(
        ISettingsStore settings,
        HolidayUpdater holidays,
        WeatherUpdater weather,
        PlaceSearch places,
        DataChangeHub hub,
        IUiDispatcher ui,
        IClock clock,
        ILogger<ExternalPageViewModel> logger)
        : this(settings)
    {
        _services = new Services(holidays, weather, places, hub, ui, clock, logger);
    }

    /// <summary>すべての外部通信を止める（F-199）。</summary>
    public bool OfflineMode
    {
        get => _settings.Current.External.OfflineMode;
        set
        {
            if (value == OfflineMode)
            {
                return;
            }
            _settings.Update(s => s.External.OfflineMode = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOnline));
            SearchPlacesCommand.NotifyCanExecuteChanged();
            RefreshNowCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>オフラインモードでないとき（個別の項目を触れる状態）。</summary>
    public bool IsOnline => !OfflineMode;

    public bool HolidaysEnabled
    {
        get => _settings.Current.External.HolidaysEnabled;
        set
        {
            if (value == HolidaysEnabled)
            {
                return;
            }
            _settings.Update(s => s.External.HolidaysEnabled = value);
            OnPropertyChanged();
        }
    }

    /// <summary>「平日のみ」の繰り返しが祝日に当たったら翌営業日へ送る（F-193）。</summary>
    public bool ShiftWeekdayRecurrenceOnHolidays
    {
        get => _settings.Current.External.ShiftWeekdayRecurrenceOnHolidays;
        set
        {
            if (value == ShiftWeekdayRecurrenceOnHolidays)
            {
                return;
            }
            _settings.Update(s => s.External.ShiftWeekdayRecurrenceOnHolidays = value);
            OnPropertyChanged();
        }
    }

    public bool WeatherEnabled
    {
        get => _settings.Current.External.WeatherEnabled;
        set
        {
            if (value == WeatherEnabled)
            {
                return;
            }
            _settings.Update(s => s.External.WeatherEnabled = value);
            OnPropertyChanged();
        }
    }

    public string WeatherLocationName =>
        _settings.Current.External.WeatherLocation is { } location
            ? $"場所: {location.Name}"
            : "場所は未設定です。下で地名を探して選ぶと、その場所の天気を取ります";

    /// <summary>メモの URL を Microlink に送ってタイトルを取る（既定 OFF）。</summary>
    public bool LinkPreviewEnabled
    {
        get => _settings.Current.External.LinkPreviewEnabled;
        set
        {
            if (value == LinkPreviewEnabled)
            {
                return;
            }
            _settings.Update(s => s.External.LinkPreviewEnabled = value);
            OnPropertyChanged();
        }
    }

    public bool UpdateCheckEnabled
    {
        get => _settings.Current.External.UpdateCheckEnabled;
        set
        {
            if (value == UpdateCheckEnabled)
            {
                return;
            }
            _settings.Update(s => s.External.UpdateCheckEnabled = value);
            OnPropertyChanged();
        }
    }

    // ---- 天気の場所 ----

    /// <summary>探す地名（送るのはこの文字だけ）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchPlacesCommand))]
    private string _placeQuery = "";

    /// <summary>探した結果。</summary>
    public ObservableCollection<PlaceCandidate> Places { get; } = [];

    /// <summary>探した結果の一言（見つからない・探せなかった）。無ければ null。</summary>
    [ObservableProperty]
    private string? _searchMessage;

    // ---- 取得の状況 ----

    /// <summary>祝日を取った状況（「2026年・2027年を取得済み（9月23日 22:35）」）。</summary>
    [ObservableProperty]
    private string _holidayStatus = "";

    /// <summary>天気を取った状況。</summary>
    [ObservableProperty]
    private string _weatherStatus = "";

    /// <summary>「今すぐ取得」の結果の一言。無ければ null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefreshMessage))]
    private string? _refreshMessage;

    public bool HasRefreshMessage => RefreshMessage is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshNowCommand))]
    private bool _isRefreshing;

    /// <summary>取得の部品があるか（無ければ場所の検索・今すぐ取得・状況の欄を出さない）。</summary>
    public bool CanFetch => _services is not null;

    /// <summary>ページを開いたとき: 取得の状況を読み、取得が済んだら読み直すよう購読する（閉じたら <see cref="Detach"/>）。</summary>
    public async Task AttachAsync()
    {
        if (_services is null)
        {
            return;
        }
        _services.Hub.Changed -= OnDataChanged;
        _services.Hub.Changed += OnDataChanged;
        await ReloadQuietlyAsync();
    }

    public void Detach()
    {
        if (_services is not null)
        {
            _services.Hub.Changed -= OnDataChanged;
        }
    }

    /// <summary>取得の状況を読み直す。</summary>
    public async Task LoadStatusAsync()
    {
        if (_services is not { } s)
        {
            return;
        }
        var (holidaysAt, years) = await Task.Run(() => s.Holidays.GetStatusAsync());
        HolidayStatus = holidaysAt is { } h && years.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{years[0]}〜{years[^1]}年を取得済み（{When(h)}）")
            : "まだ取得していません";

        var weatherAt = await Task.Run(() => s.Weather.GetLastFetchedAtAsync());
        WeatherStatus = _settings.Current.External.WeatherLocation is null
            ? "場所を選ぶと取得します"
            : weatherAt is { } w ? $"最後に取得: {When(w)}（16日先まで）" : "まだ取得していません";
    }

    private bool CanSearch() => _services is not null && IsOnline && PlaceQuery.Trim().Length > 0;

    /// <summary>地名で場所を探す（送るのは入力した地名だけ）。</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchPlacesAsync()
    {
        if (_services is not { } s)
        {
            return;
        }
        SearchMessage = "探しています…";
        var found = await Task.Run(() => s.Places.SearchAsync(PlaceQuery));
        Places.Clear();
        if (found is null)
        {
            SearchMessage = "探せませんでした。通信できる状態か確かめて、時間をおいてもう一度試してください";
            return;
        }
        foreach (var place in found)
        {
            Places.Add(place);
        }
        // Open-Meteo は2文字までだと完全一致でしか探さない（「大阪」では出ず「大阪市」「Osaka」なら出る）
        SearchMessage = found.Count == 0
            ? $"「{PlaceQuery.Trim()}」は見つかりませんでした。「大阪市」のように市町村名まで入れるか、ローマ字（Osaka）で探してください"
            : null;
    }

    /// <summary>探した場所を天気の場所にする（緯度経度は丸める）。選んだら天気を ON にする。</summary>
    [RelayCommand]
    private void SelectPlace(PlaceCandidate? place)
    {
        if (place is null)
        {
            return;
        }
        var location = place.ToLocation();
        _settings.Update(s =>
        {
            s.External.WeatherLocation = location;
            s.External.WeatherEnabled = true;
        });
        Places.Clear();
        PlaceQuery = "";
        SearchMessage = null;
        OnPropertyChanged(nameof(WeatherLocationName));
        OnPropertyChanged(nameof(WeatherEnabled));
        WeatherStatus = "取得しています…";
    }

    private bool CanRefreshNow() => _services is not null && IsOnline && !IsRefreshing;

    /// <summary>祝日と天気を今すぐ取り直す（期限を待たない）。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshNow))]
    private async Task RefreshNowAsync()
    {
        if (_services is not { } s)
        {
            return;
        }
        IsRefreshing = true;
        RefreshMessage = "取得しています…";
        try
        {
            var external = _settings.Current.External;
            var holidays = await Task.Run(() => s.Holidays.RefreshAsync(force: true));
            var weather = await Task.Run(() => s.Weather.RefreshAsync(force: true));
            var wanted = (external.HolidaysEnabled ? 1 : 0) + (external.WeatherEnabled && external.WeatherLocation is not null ? 1 : 0);
            var got = (holidays > 0 ? 1 : 0) + (weather ? 1 : 0);
            RefreshMessage = wanted == 0
                ? "取得する項目がオフになっています"
                : got == wanted
                    ? "取得しました"
                    : "一部を取得できませんでした。時間をおいてもう一度試してください（前に取った分はそのまま使います）";
            await LoadStatusAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>裏の取得が終わった（ExternalCache）ら状況を読み直す。発火はどのスレッドでも来るので UI に戻す。</summary>
    private void OnDataChanged(object? sender, DataChangedEventArgs e)
    {
        if (_services is { } s && e.Kinds.HasFlag(DataChangeKind.ExternalCache))
        {
            s.Ui.Post(() => _ = ReloadQuietlyAsync());
        }
    }

    private async Task ReloadQuietlyAsync()
    {
        try
        {
            await LoadStatusAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // 状況の表示だけなので、読めなくても設定画面は使える
            _services?.Logger.LogWarning(ex, "外部データの取得状況を読めませんでした");
        }
    }

    private string When(DateTime utc) =>
        _services!.Clock.ToLocal(utc).ToString("M月d日 H:mm", CultureInfo.InvariantCulture);

    /// <summary>取得に使う部品（値の保存だけのときは無い）。</summary>
    private sealed record Services(
        HolidayUpdater Holidays,
        WeatherUpdater Weather,
        PlaceSearch Places,
        DataChangeHub Hub,
        IUiDispatcher Ui,
        IClock Clock,
        ILogger Logger);
}

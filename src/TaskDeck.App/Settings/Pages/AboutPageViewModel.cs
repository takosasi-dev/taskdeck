using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDeck.Core.Time;
using TaskDeck.Data.External;

namespace TaskDeck.App.Settings.Pages;

/// <summary>使っているライブラリ1件（TaskDeck について）。</summary>
public sealed record LibraryInfo(string Name, string License, string Use)
{
    /// <summary>一覧の項目としての読み上げ名（UI Automation は項目の ToString を使う）。</summary>
    public override string ToString() => $"{Name}（{License}）";
}

/// <summary>
/// TaskDeck について（バージョン・更新の確認・フォルダ・ライセンス）。担当: 波1-D、更新の確認は波3-K。
/// 更新の確認（F-198）は結果を控えめに出すだけ（ダイアログもバッジも出さない。ダウンロードもしない）。
/// </summary>
public sealed partial class AboutPageViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly UpdateChecker? _updates;
    private readonly IClock? _clock;
    private DateTime? _checkedAt;

    public AboutPageViewModel(AppPaths paths)
    {
        _paths = paths;
    }

    public AboutPageViewModel(AppPaths paths, UpdateChecker updates, IClock clock)
        : this(paths)
    {
        _updates = updates;
        _clock = clock;
    }

    public string Version =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string DataDirectory => _paths.DataDirectory;

    public string LogDirectory => _paths.LogDirectory;

    /// <summary>開発用のデータフォルダで動かしているか（開発時だけの項目を出すため）。</summary>
    public bool IsDevelopment => _paths.IsDevelopment;

    /// <summary>同梱しているもの（Directory.Packages.props のパッケージ。ライセンスは NuGet の表記どおり）。</summary>
    public IReadOnlyList<LibraryInfo> Libraries { get; } =
    [
        new(".NET 10 / WPF", "MIT", "アプリの土台"),
        new("Microsoft.Extensions（Hosting・Http・Logging）", "MIT", "起動処理・依存性の注入・外部通信"),
        new("WPF-UI 4.3", "MIT", "Windows 11 の見た目のコントロール"),
        new("CommunityToolkit.Mvvm 8.4", "MIT", "MVVM（ObservableObject・RelayCommand）"),
        new("CommunityToolkit.WinUI.Notifications 7.1", "MIT", "トースト通知"),
        new("Entity Framework Core 10 / Microsoft.Data.Sqlite", "MIT", "データベース"),
        new("SQLitePCLRaw", "Apache-2.0", "SQLite の呼び出し"),
        new("SQLite", "パブリックドメイン", "データベース本体"),
        new("Serilog.Extensions.Hosting / Serilog.Sinks.File", "Apache-2.0", "ログ"),
        new("Hardcodet.NotifyIcon.Wpf 2.0", "CPOL 1.02", "タスクトレイ"),
        new("Ical.Net 5", "MIT", "繰り返し（RRULE）の計算"),
    ];

    /// <summary>使っている外部サービス（どれも任意。送るものを併記する。NFR 6.4）。</summary>
    public IReadOnlyList<LibraryInfo> ExternalServices { get; } =
    [
        new("内閣府「国民の祝日」", "祝日", "送るもの: なし（公開されている CSV を1日1回まで取るだけ）"),
        new("Open-Meteo.com", "天気（CC BY 4.0）", "送るもの: 小数第2位に丸めた緯度経度・探すときに入力した地名"),
        new("Microlink", "URL のタイトル（既定オフ）", "送るもの: メモに書いた URL（オンにしたときだけ）"),
        new("GitHub Releases", "更新の確認", "送るもの: リポジトリ名"),
    ];

    // ---- 更新の確認 ----

    /// <summary>結果の一文（「最新です（バージョン 0.1.0）」「新しい版があります: v0.2.0」など）。</summary>
    [ObservableProperty]
    private string _updateStatusText = "";

    /// <summary>最後に確かめた日時（無ければ空）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckedText))]
    private string _updateCheckedText = "";

    public bool HasUpdateCheckedText => UpdateCheckedText.Length > 0;

    /// <summary>新しい版がある（リリースのページを開くボタンを出す）。</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _canCheck;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isChecking;

    /// <summary>更新の確認の欄を出すか（部品が無い＝値の表示だけのときは出さない）。</summary>
    public bool ShowsUpdates => _updates is not null;

    /// <summary>ページを開いたとき: 覚えている結果を出す（通信しない）。</summary>
    public async Task LoadAsync()
    {
        if (_updates is null)
        {
            return;
        }
        Show(await Task.Run(() => _updates.GetStatusAsync()));
    }

    private bool CanCheckNow() => CanCheck && !IsChecking;

    /// <summary>今すぐ確かめる（1日1回の待ちを飛ばす）。</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        if (_updates is null)
        {
            return;
        }
        IsChecking = true;
        try
        {
            var before = _checkedAt;
            UpdateStatusText = "確認しています…";
            Show(await Task.Run(() => _updates.CheckAsync(force: true)));
            if (CanCheck && _checkedAt == before)
            {
                UpdateStatusText = "確認できませんでした。時間をおいて試してください";
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>リリースのページを既定のブラウザで開く（ダウンロードは人が選ぶ）。</summary>
    [RelayCommand]
    private void OpenReleases()
    {
        if (_updates?.ReleasesPage is { } page && page.StartsWith("https://github.com/", StringComparison.Ordinal))
        {
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>状態を画面の文にする。</summary>
    internal static string Describe(UpdateStatus status, string currentVersion) => status.State switch
    {
        UpdateState.NotConfigured => "公開先が決まると、ここで新しい版が出ていないか確かめられるようになります",
        UpdateState.Disabled => "更新の確認はオフです（「外部サービス」で切り替えられます）",
        UpdateState.Offline => "オフラインモードのため確認していません",
        UpdateState.UpToDate => $"最新です（バージョン {currentVersion}）",
        UpdateState.Available => $"新しい版があります: {status.LatestVersion}（今はバージョン {currentVersion}）",
        _ => status.CheckedAtUtc is null ? "まだ確認していません" : "確認できませんでした。時間をおいて試してください",
    };

    private void Show(UpdateStatus status)
    {
        _checkedAt = status.CheckedAtUtc;
        UpdateStatusText = Describe(status, _updates!.CurrentVersion);
        UpdateCheckedText = status.CheckedAtUtc is { } at && _clock is not null
            ? "最後に確認: " + _clock.ToLocal(at).ToString("yyyy/MM/dd H:mm", CultureInfo.InvariantCulture)
            : "";
        HasUpdate = status.State == UpdateState.Available;
        CanCheck = status.State is UpdateState.Unknown or UpdateState.UpToDate or UpdateState.Available;
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
    }
}

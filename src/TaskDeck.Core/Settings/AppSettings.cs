using TaskDeck.Core.Queries;

namespace TaskDeck.Core.Settings;

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// settings.json の中身（設定画面の全項目＋ウィンドウ配置などの UI 状態）。
/// 壊れていたら settings.json.bak に退避して既定値で起動する。値の追加は既定値つきで行う（古いファイルとの互換のため）。
/// </summary>
public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public CalendarSettings Calendar { get; set; } = new();
    public DataSettings Data { get; set; } = new();
    public ExternalSettings External { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public List<SavedFilter> SavedFilters { get; set; } = [];
    /// <summary>初回起動の3画面を終えたか（スキップを含む）。</summary>
    public bool FirstRunCompleted { get; set; }
}

public sealed class GeneralSettings
{
    /// <summary>起動時に開くビュー（ViewKey.ToString()）。</summary>
    public string StartView { get; set; } = ViewKey.Today.ToString();
    /// <summary>Windows ログオン時に自動起動する（F-094、既定 ON）。</summary>
    public bool LaunchAtLogin { get; set; } = true;
    /// <summary>ログを Debug まで出す。</summary>
    public bool VerboseLogging { get; set; }
}

public sealed class AppearanceSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    /// <summary>null ならシステムのアクセント色に従う。"#RRGGBB" なら固定。</summary>
    public string? AccentColorHex { get; set; }
    /// <summary>null なら OS の「アニメーション効果」に従う。</summary>
    public bool? ReduceMotion { get; set; }
    /// <summary>タスクを選んだら詳細ペインを自動で開く（S-06）。</summary>
    public bool AutoShowDetailPane { get; set; } = true;
    /// <summary>行を詰めて表示（タスク行 36px）。既定は 42px。</summary>
    public bool CompactRows { get; set; }
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool DailySummaryEnabled { get; set; } = true;
    public TimeOnly DailySummaryTime { get; set; } = new(8, 0);
    /// <summary>日付のみの期限に相対通知を付けたときの基準時刻（F-124、既定 9:00）。</summary>
    public TimeOnly DefaultReminderTime { get; set; } = new(9, 0);
    public int SnoozeMinutes { get; set; } = 30;
    public bool OverdueNoticeEnabled { get; set; } = true;
    /// <summary>「通知を1時間止める」の期限（UTC）。</summary>
    public DateTime? PausedUntilUtc { get; set; }
}

public sealed class HotkeySettings
{
    public string QuickInput { get; set; } = "Ctrl+Shift+Space";
    public string ShowMainWindow { get; set; } = "Ctrl+Shift+T";
    public string FocusMode { get; set; } = "Ctrl+Shift+F";
}

public sealed class CalendarSettings
{
    /// <summary>週の開始を月曜にする（F-125、既定は日曜）。</summary>
    public bool WeekStartsOnMonday { get; set; }
}

public sealed class DataSettings
{
    /// <summary>Markdown の出力先（F-109）。null なら毎回たずねる。</summary>
    public string? MarkdownExportFolder { get; set; }
}

public sealed class ExternalSettings
{
    /// <summary>すべての外部通信を止める（F-199）。</summary>
    public bool OfflineMode { get; set; }
    public bool HolidaysEnabled { get; set; } = true;
    /// <summary>「平日のみ」の繰り返しが祝日に当たったら翌営業日へ送る（F-193）。</summary>
    public bool ShiftWeekdayRecurrenceOnHolidays { get; set; } = true;
    /// <summary>場所を選ぶまでは取得しない。</summary>
    public bool WeatherEnabled { get; set; }
    public WeatherLocation? WeatherLocation { get; set; }
    /// <summary>メモの URL を Microlink に送ってタイトルを取る（既定 OFF）。</summary>
    public bool LinkPreviewEnabled { get; set; }
    public bool UpdateCheckEnabled { get; set; } = true;
}

/// <summary>天気の場所。緯度経度は小数第2位に丸めて持つ（約1km）。</summary>
public sealed record WeatherLocation(string Name, double Latitude, double Longitude);

public sealed class WindowSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 800;
    public bool Maximized { get; set; }
    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 220;
    public double DetailPaneWidth { get; set; } = 320;
}

/// <summary>保存済みフィルタ（F-05A）。条件は相対指定のまま持つ。</summary>
public sealed class SavedFilter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public TaskQuery Query { get; set; } = new();
}

/// <summary>
/// 設定の読み書き。Update は保存まで行い、Changed を発火する（どのスレッドから呼ばれても良い。発火は呼んだスレッドで）。
/// </summary>
public interface ISettingsStore
{
    AppSettings Current { get; }

    void Update(Action<AppSettings> mutate);

    event EventHandler? Changed;
}

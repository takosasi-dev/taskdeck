using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.App.Residency;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Settings.Pages;

/// <summary>
/// 全般（起動時の動作・自動起動・ログ）。担当: 波1-D（自動起動の反映は波3-H: AutoStartService が設定の変更を見て Run に書く）。
/// 値は ISettingsStore.Update で即保存する（保存ボタンは無い）。
/// </summary>
public sealed partial class GeneralPageViewModel(ISettingsStore settings, ResidencySwitches? switches = null) : ObservableObject
{
    /// <summary>自動起動のカードの下に出す一言（開発用フォルダでは Windows に登録しないこと）。無ければ空。</summary>
    public string LaunchAtLoginNote => switches is { WriteRunKey: false }
        ? "開発用フォルダで動かしているため、Windows には登録しません（設定だけ保存します）"
        : "";

    public bool HasLaunchAtLoginNote => LaunchAtLoginNote.Length > 0;

    /// <summary>「起動時に開くビュー」に出す組み込みビュー（表示順）。</summary>
    public static IReadOnlyList<ViewKey> StartViews { get; } =
    [
        ViewKey.Today,
        ViewKey.Upcoming,
        ViewKey.All,
        ViewKey.Completed,
    ];

    public IReadOnlyList<string> StartViewNames { get; } =
        [.. StartViews.Select(v => BuiltInViews.TitleOf(v.Kind))];

    /// <summary>StartViews の何番目か。設定に知らないビューが入っていたら「今日」に寄せる。</summary>
    public int StartViewIndex
    {
        get
        {
            if (!ViewKey.TryParse(settings.Current.General.StartView, out var key))
            {
                return 0;
            }
            var index = StartViews.ToList().IndexOf(key);
            return index < 0 ? 0 : index;
        }
        set
        {
            if (value < 0 || value >= StartViews.Count || value == StartViewIndex)
            {
                return;
            }
            settings.Update(s => s.General.StartView = StartViews[value].ToString());
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> WeekStartChoices { get; } = ["日曜日", "月曜日"];

    /// <summary>週の始まり（F-125）。0: 日曜 / 1: 月曜。日付ピッカーとカレンダーの並びに使う。</summary>
    public int WeekStartIndex
    {
        get => settings.Current.Calendar.WeekStartsOnMonday ? 1 : 0;
        set
        {
            if (value == WeekStartIndex || value is < 0 or > 1)
            {
                return;
            }
            settings.Update(s => s.Calendar.WeekStartsOnMonday = value == 1);
            OnPropertyChanged();
        }
    }

    public bool LaunchAtLogin
    {
        get => settings.Current.General.LaunchAtLogin;
        set
        {
            if (value == LaunchAtLogin)
            {
                return;
            }
            settings.Update(s => s.General.LaunchAtLogin = value);
            OnPropertyChanged();
        }
    }

    public bool VerboseLogging
    {
        get => settings.Current.General.VerboseLogging;
        set
        {
            if (value == VerboseLogging)
            {
                return;
            }
            settings.Update(s => s.General.VerboseLogging = value);
            OnPropertyChanged();
        }
    }
}

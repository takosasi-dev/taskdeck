using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDeck.App.Services;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Settings.Pages;

/// <summary>固定アクセント色の見本1つ。Hex は設定に保存する値（ライトの色）、DisplayHex は今のテーマで見える色。</summary>
public sealed record AccentSwatch(string Name, string Hex, string DisplayHex, bool IsCurrent)
{
    /// <summary>一覧の項目としての読み上げ名（UI Automation は項目の ToString を使う）。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 外観（テーマ・アクセント・アニメーション・行の高さ・詳細ペイン）。担当: 波1-D。
/// 値は即保存（ISettingsStore.Update）し、見た目は ThemeService.ApplyFromSettings でその場で反映する。
/// </summary>
public sealed partial class AppearancePageViewModel(ISettingsStore settings, ThemeService theme) : ObservableObject
{
    public IReadOnlyList<string> ReduceMotionChoices { get; } = ["Windows の設定に従う", "減らす", "減らさない"];

    public IReadOnlyList<string> RowHeightChoices { get; } = ["標準（42px）", "詰める（36px）"];

    public bool IsThemeLight
    {
        get => settings.Current.Appearance.Theme == AppThemeMode.Light;
        set => SetTheme(value, AppThemeMode.Light);
    }

    public bool IsThemeDark
    {
        get => settings.Current.Appearance.Theme == AppThemeMode.Dark;
        set => SetTheme(value, AppThemeMode.Dark);
    }

    public bool IsThemeSystem
    {
        get => settings.Current.Appearance.Theme == AppThemeMode.System;
        set => SetTheme(value, AppThemeMode.System);
    }

    /// <summary>アクセント色を Windows の設定に合わせる（オフなら固定色。最初は先頭の青）。</summary>
    public bool UseSystemAccent
    {
        get => settings.Current.Appearance.AccentColorHex is null;
        set
        {
            if (value == UseSystemAccent)
            {
                return;
            }
            settings.Update(s => s.Appearance.AccentColorHex = value ? null : ThemeService.AccentChoices[0].LightHex);
            AfterAppearanceChanged();
        }
    }

    /// <summary>設定に保存されている固定アクセント色（Windows に合わせているときは null）。</summary>
    public string? AccentColorHex => settings.Current.Appearance.AccentColorHex;

    public string AccentDescription => UseSystemAccent
        ? "Windows の設定に合わせています"
        : "下の色から選んでいます（ライトとダークで見やすい明るさに合わせます）";

    /// <summary>固定アクセント色の見本（今のテーマで見える色で並べる）。</summary>
    public IReadOnlyList<AccentSwatch> AccentSwatches
    {
        get
        {
            var current = AccentColorHex is { } hex ? ThemeService.AccentForTheme(hex, dark: false) : null;
            return
            [
                .. ThemeService.AccentChoices.Select(c => new AccentSwatch(
                    c.Name,
                    c.LightHex,
                    theme.IsDark ? c.DarkHex : c.LightHex,
                    string.Equals(c.LightHex, current, StringComparison.OrdinalIgnoreCase))),
            ];
        }
    }

    /// <summary>0: OS に従う / 1: 減らす / 2: 減らさない。</summary>
    public int ReduceMotionIndex
    {
        get => settings.Current.Appearance.ReduceMotion switch
        {
            null => 0,
            true => 1,
            false => 2,
        };
        set
        {
            if (value == ReduceMotionIndex || value is < 0 or > 2)
            {
                return;
            }
            bool? stored = value switch { 1 => true, 2 => false, _ => (bool?)null };
            settings.Update(s => s.Appearance.ReduceMotion = stored);
            AfterAppearanceChanged();
        }
    }

    /// <summary>OS に従っている項目は説明でそう書き、いまどちらになっているかも出す（UI 設計書 17章・19.3）。</summary>
    public string ReduceMotionDescription => ReduceMotionIndex == 0
        ? $"Windows の「アニメーション効果」に従っています（いまは{(theme.ReduceMotion ? "減らしています" : "動かしています")}）"
        : "この設定で Windows の「アニメーション効果」を上書きしています";

    /// <summary>0: 標準（42px） / 1: 詰める（36px）。</summary>
    public int RowHeightIndex
    {
        get => settings.Current.Appearance.CompactRows ? 1 : 0;
        set
        {
            if (value == RowHeightIndex || value is < 0 or > 1)
            {
                return;
            }
            settings.Update(s => s.Appearance.CompactRows = value == 1);
            OnPropertyChanged();
        }
    }

    public bool AutoShowDetailPane
    {
        get => settings.Current.Appearance.AutoShowDetailPane;
        set
        {
            if (value == AutoShowDetailPane)
            {
                return;
            }
            settings.Update(s => s.Appearance.AutoShowDetailPane = value);
            OnPropertyChanged();
        }
    }

    /// <summary>テーマが変わったとき（Windows 側の切替を含む）に、テーマで変わる表示を読み直す。ページが呼ぶ。</summary>
    public void OnThemeChanged()
    {
        OnPropertyChanged(nameof(AccentSwatches));
        OnPropertyChanged(nameof(ReduceMotionDescription));
    }

    [RelayCommand]
    private void PickAccent(string? hex)
    {
        if (hex is null || string.Equals(hex, AccentColorHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        settings.Update(s => s.Appearance.AccentColorHex = hex);
        AfterAppearanceChanged();
    }

    private void SetTheme(bool selected, AppThemeMode mode)
    {
        if (!selected || settings.Current.Appearance.Theme == mode)
        {
            return;
        }
        settings.Update(s => s.Appearance.Theme = mode);
        AfterAppearanceChanged();
    }

    /// <summary>設定を書いたあと、その場で画面に反映する。</summary>
    private void AfterAppearanceChanged()
    {
        theme.ApplyFromSettings();
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(UseSystemAccent));
        OnPropertyChanged(nameof(AccentColorHex));
        OnPropertyChanged(nameof(AccentDescription));
        OnPropertyChanged(nameof(ReduceMotionIndex));
        OnThemeChanged();
    }
}

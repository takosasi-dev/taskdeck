using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TaskDeck.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TaskDeck.App.Services;

/// <summary>設定の「アクセントカラー」で選べる固定色。ライトとダークで別の値を使う（同じ色相で、どちらの地でも文字に使える明るさ）。</summary>
public sealed record AccentChoice(string Name, string LightHex, string DarkHex);

/// <summary>
/// テーマの切替（F-121）。自前のトークン辞書（Resources/Themes/Light.xaml・Dark.xaml・HighContrast.xaml）を
/// 差し替え、WPF-UI のテーマとアクセント色も合わせる。担当: 波1-D。
///
/// 反映するもの:
/// - 設定のライト / ダーク / システム（システムのときは Windows のモード変更にその場で追従する）
/// - アクセント色（設定の固定色 → 無ければシステムの色 → 取れなければ #0067C0 / #4CC2FF）
/// - Windows のハイコントラスト（有効な間は HighContrast.xaml に切り替える）
/// - 「アニメーションを減らす」（設定が null なら OS の「アニメーション効果」に従う）
///
/// 切替は起動中いつでも効く（色はすべて DynamicResource で参照するため）。
/// 色を DynamicResource で引けないもの（コンバータが作るブラシなど）は ThemeChanged を受けて作り直す。
/// </summary>
public sealed class ThemeService(ISettingsStore settings, ILogger<ThemeService>? logger = null)
{
    private const string ThemeFolder = "/Resources/Themes/";
    private const string LightAccentFallback = "#0067C0";
    private const string DarkAccentFallback = "#4CC2FF";

    /// <summary>
    /// 固定アクセント色の候補。設定（AccentColorHex）にはライトの値を保存し、ダークでは組の相方に読み替える。
    /// どの色もテーマ辞書の全部の面の上で文字として 4.5:1、アクセント地の上の文字（Text.OnAccent）も 4.5:1 を満たす:
    ///   node tools/validate-palette.js --accents "#0067C0/#4CC2FF,#BF4600/#FF8B56,#805DB0/#AF87E8,#0E7C5A/#80DDB6,#BD3C76/#F569A3,#876A00/#EDCE78"
    /// （2026-09-23 合格。結果の表は tools/palette-results.md）。プロジェクト色と同じ色相だが、プロジェクト色は
    /// 白地だけで 4.5:1 なので、Surface.Window などでも通るよう OKLab の明度を少しずらしてある。
    /// </summary>
    public static IReadOnlyList<AccentChoice> AccentChoices { get; } =
    [
        new("青", "#0067C0", "#4CC2FF"),
        new("橙", "#BF4600", "#FF8B56"),
        new("紫", "#805DB0", "#AF87E8"),
        new("緑", "#0E7C5A", "#80DDB6"),
        new("赤紫", "#BD3C76", "#F569A3"),
        new("黄", "#876A00", "#EDCE78"),
    ];

    // アクセントに付いてくるブラシ（自前のトークンと、WPF-UI のコントロールが使うキー）。ThemeService がアプリ全体
    // （Application.Resources の直下。マージされた辞書より優先される）に置く。テーマ辞書の中のブラシは読み込んだ時の色で
    // 固まり、DynamicResource で書いても後から置いた色に追従しない（辞書の中の Freezable は更新の通知を受けない）ため。
    // テーマ辞書（Light/Dark/HighContrast.xaml）にも同じキーがあり、そちらは取れなかったときの値。
    private static readonly string[] AccentDefaultKeys =
    [
        "AccentDefaultBrush", "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "CheckBoxCheckBackgroundFillChecked",
        "RadioButtonOuterEllipseCheckedStroke", "ComboBoxItemPillFillBrush", "TextControlFocusedBorderBrush",
        "ListViewItemPillFillBrush", "NavigationViewSelectionIndicatorForeground", "HyperlinkButtonForeground",
        "SystemFillColorAttentionBrush",
    ];

    private static readonly string[] AccentHoverKeys =
    [
        "AccentHoverBrush", "ToggleSwitchFillOnPointerOver", "ToggleSwitchStrokeOnPointerOver",
        "CheckBoxCheckBackgroundFillCheckedPointerOver", "RadioButtonOuterEllipseCheckedStrokePointerOver",
        "HyperlinkButtonForegroundPointerOver",
    ];

    private static readonly string[] AccentPressedKeys =
    [
        "AccentPressedBrush", "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOnPressed",
        "CheckBoxCheckBackgroundFillCheckedPressed", "HyperlinkButtonForegroundPressed",
    ];

    private static readonly string[] AccentColorKeys = ["AccentDefaultColor", "AccentHoverColor", "AccentPressedColor"];

    private const string AccentBorderKey = "AccentControlElevationBorderBrush";

    private AppThemeMode _mode = AppThemeMode.System;
    private bool _hooked;
    private string? _lastSystemState;

    public bool IsDark { get; private set; }

    /// <summary>Windows のハイコントラストが有効（HighContrast.xaml を使っている）。</summary>
    public bool IsHighContrast { get; private set; }

    /// <summary>
    /// アニメーションを減らす（UI 設計書 7章）。設定が null のときは OS の「アニメーション効果」に従う。
    /// アニメーションを書く側は、true なら Duration を 0 にする（Durations.xaml の値を使わずに即座に切り替える）。
    /// </summary>
    public bool ReduceMotion { get; private set; }

    /// <summary>テーマが変わった（UI スレッドで発火）。</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>ReduceMotion が変わった（UI スレッドで発火）。</summary>
    public event EventHandler? ReduceMotionChanged;

    public void ApplyFromSettings() => Apply(settings.Current.Appearance.Theme);

    public void Apply(AppThemeMode mode)
    {
        _mode = mode;
        if (Application.Current is null)
        {
            UpdateReduceMotion();
            return; // 画面が無いとき（テストなど）は辞書に触らない
        }
        HookSystemEvents();

        var highContrast = SystemParameters.HighContrast;
        var dark = mode switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.Light => false,
            _ => IsSystemDark(),
        };

        // ハイコントラストの Color は x:Static で読み込み時に固まるので、配色が変わったかもしれないときは読み直す
        SwapTokenDictionary(highContrast ? "HighContrast.xaml" : dark ? "Dark.xaml" : "Light.xaml", reload: highContrast);
        var wpfUiTheme = highContrast
            ? ApplicationTheme.HighContrast
            : dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(wpfUiTheme, WindowBackdropType.None, updateAccent: false);
        foreach (Window window in Application.Current.Windows)
        {
            RestoreWindowBackground(window);
        }
        ApplyAccent(dark, highContrast);

        IsDark = dark;
        IsHighContrast = highContrast;
        _lastSystemState = CurrentSystemState();
        UpdateReduceMotion();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Windows の「アプリのモード」がダークか。</summary>
    public static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    public static Color? TryParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        try
        {
            return ColorConverter.ConvertFromString(hex) as Color?;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 設定に保存された固定アクセント色を、テーマに合わせた値にする。候補（AccentChoices）の色ならライト/ダークの組で読み替え、
    /// 候補に無い色（settings.json を手で書いたなど）はそのまま返す。
    /// </summary>
    public static string AccentForTheme(string hex, bool dark)
    {
        foreach (var choice in AccentChoices)
        {
            if (string.Equals(choice.LightHex, hex, StringComparison.OrdinalIgnoreCase)
                || string.Equals(choice.DarkHex, hex, StringComparison.OrdinalIgnoreCase))
            {
                return dark ? choice.DarkHex : choice.LightHex;
            }
        }
        return hex;
    }

    /// <summary>
    /// システムのアクセント色（ライトは AccentDark1、ダークは AccentLight2）。
    /// 地の明るさに合わせた段階を使う（そのままの SystemAccentColor は白地で薄すぎる）。取れなければ null。
    /// </summary>
    public Color? TryGetSystemAccent(bool dark)
    {
        // まずレジストリの色の組（UISettings と同じ8段階。明るい3・基準・暗い3・補色の順に1色4バイト RGB_）を読む。
        // WinRT（UISettings）は読み込みに時間がかかる（起動が約0.1秒遅れる。NFR 3.2）ので、読めないときだけ使う
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
        {
            if (key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } palette)
            {
                var i = dark ? 1 * 4 : 4 * 4; // AccentLight2 / AccentDark1
                return Color.FromRgb(palette[i], palette[i + 1], palette[i + 2]);
            }
        }
        return TryGetSystemAccentFromWinRt(dark);
    }

    /// <summary>WinRT の型を使うのはここだけ（別のメソッドにして、レジストリで足りるときは WinRT を読み込まない）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Color? TryGetSystemAccentFromWinRt(bool dark)
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var color = ui.GetColorValue(dark
                ? Windows.UI.ViewManagement.UIColorType.AccentLight2
                : Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or TypeInitializationException
            or PlatformNotSupportedException or InvalidCastException or NotSupportedException)
        {
            // 取れないだけなら既定色で続ける（アプリを止める理由にならない）
            logger?.LogWarning(ex, "システムのアクセント色を取得できませんでした。既定のアクセント色を使います");
            return null;
        }
    }

    /// <summary>アクセント色を自前のトークンと WPF-UI の両方へ。ハイコントラストのときは SystemColors の Highlight に任せる。</summary>
    private void ApplyAccent(bool dark, bool highContrast)
    {
        var resources = Application.Current.Resources;
        if (highContrast)
        {
            ApplicationAccentColorManager.Apply(SystemColors.HighlightColor, ApplicationTheme.HighContrast);
            // HighContrast.xaml の SystemColors 由来の値を隠さないよう、直下の上書きを取り除く
            foreach (var key in AccentColorKeys.Concat(AccentDefaultKeys).Concat(AccentHoverKeys).Concat(AccentPressedKeys).Append(AccentBorderKey))
            {
                resources.Remove(key);
            }
            return;
        }

        var fixedHex = settings.Current.Appearance.AccentColorHex;
        var accent = (fixedHex is null ? null : TryParseColor(AccentForTheme(fixedHex, dark)))
            ?? TryGetSystemAccent(dark)
            ?? (Color)ColorConverter.ConvertFromString(dark ? DarkAccentFallback : LightAccentFallback);
        // ホバー・押下は同じ色相で明るさをずらす（UI 設計書 2.3 の既定値の比率。ライトは暗く、ダークはホバーで明るく）
        var hover = dark ? Lighten(accent, 0.12) : Scale(accent, 0.88);
        var pressed = dark ? Scale(accent, 0.88) : Scale(accent, 0.76);

        ApplicationAccentColorManager.Apply(accent, dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        // WPF-UI は受け取った色から明るさをずらした塗りを作るが、アプリの中ではトークンの色にそろえる
        // （ダークのアクセントは既に AccentLight2 なので、さらに明るくすると白に近づいてしまう）
        resources["AccentFillColorDefaultBrush"] = Brush(accent);
        resources["AccentFillColorSecondaryBrush"] = Brush(hover);
        resources["AccentFillColorTertiaryBrush"] = Brush(pressed);
        resources["AccentTextFillColorPrimaryBrush"] = Brush(accent);

        resources["AccentDefaultColor"] = accent;
        resources["AccentHoverColor"] = hover;
        resources["AccentPressedColor"] = pressed;
        foreach (var (keys, color) in new[] { (AccentDefaultKeys, accent), (AccentHoverKeys, hover), (AccentPressedKeys, pressed) })
        {
            var brush = Brush(color);
            foreach (var key in keys)
            {
                resources[key] = brush;
            }
        }
        var border = new LinearGradientBrush(hover, pressed, 90);
        border.Freeze();
        resources[AccentBorderKey] = border;
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 窓の地を Surface.Window（ApplicationBackgroundBrush）の参照に戻す。
    /// WPF-UI の FluentWindow は、作ったときとテーマを切り替えたときに Background へ「その時点のテーマの固定色」
    /// （ダーク #202020 / ライト #FAFAFA）を直接入れる。そのままだと地が Surface.Window にならず、切り替えても変わらない窓が残る。
    /// 透明な窓（AllowsTransparency）や、XAML で DynamicResource を指定している窓には触らない。
    /// </summary>
    private static void RestoreWindowBackground(Window window)
    {
        if (window is FluentWindow && !window.AllowsTransparency
            && window.ReadLocalValue(Window.BackgroundProperty) is SolidColorBrush)
        {
            window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
        }
    }

    private static Color Scale(Color color, double factor) => Color.FromRgb(
        (byte)Math.Clamp(color.R * factor, 0, 255),
        (byte)Math.Clamp(color.G * factor, 0, 255),
        (byte)Math.Clamp(color.B * factor, 0, 255));

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R + ((255 - color.R) * amount), 0, 255),
        (byte)Math.Clamp(color.G + ((255 - color.G) * amount), 0, 255),
        (byte)Math.Clamp(color.B + ((255 - color.B) * amount), 0, 255));

    private static void SwapTokenDictionary(string fileName, bool reload)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString;
            if (source is not null && source.Contains(ThemeFolder, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (!reload && index >= 0
            && merged[index].Source?.OriginalString?.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) == true)
        {
            return; // 同じ辞書を入れ直すと画面がちらつくだけ
        }
        var dictionary = new ResourceDictionary { Source = new Uri(ThemeFolder + fileName, UriKind.Relative) };
        if (index >= 0)
        {
            merged[index] = dictionary;
        }
        else
        {
            merged.Add(dictionary);
        }
    }

    private void UpdateReduceMotion()
    {
        // 設定が null なら OS の「アニメーション効果」（SPI_GETCLIENTAREAANIMATION）に従う
        var reduce = settings.Current.Appearance.ReduceMotion ?? !SystemParameters.ClientAreaAnimation;
        if (reduce == ReduceMotion)
        {
            return;
        }
        ReduceMotion = reduce;
        ReduceMotionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HookSystemEvents()
    {
        if (_hooked || Application.Current is null)
        {
            return;
        }
        _hooked = true;
        // アプリと同じ寿命なので外さない（外すのはプロセス終了時）
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // 後から開く窓（設定・ダイアログなど）も、WPF-UI が作成時に入れた固定色を Surface.Window の参照に戻す。
        // WPF-UI は窓の Loaded（クラスハンドラより後に呼ばれるインスタンスのハンドラ）で色を入れるので、その後に回す
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var window = (Window)sender;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RestoreWindowBackground(window));
        }));
    }

    /// <summary>
    /// システム側の見た目の設定（別スレッドで来る）。アプリのモード（ImmersiveColorSet）とアクセント色の変更は General、
    /// ハイコントラストは Accessibility / Color、アニメーション効果は General で届く。変化があったときだけ入れ直す。
    /// </summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle))
        {
            return;
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }
        dispatcher.BeginInvoke(() =>
        {
            SystemThemeManager.UpdateSystemThemeCache();
            if (CurrentSystemState() == _lastSystemState)
            {
                UpdateReduceMotion();
                return;
            }
            Apply(_mode);
        });
    }

    /// <summary>入れ直しが要るかの判定に使う、システム側の状態のまとめ。</summary>
    private string CurrentSystemState()
    {
        var dark = IsSystemDark();
        var accent = settings.Current.Appearance.AccentColorHex is { Length: > 0 } fixedHex
            ? fixedHex
            : TryGetSystemAccent(dark)?.ToString() ?? "-";
        return $"{dark}/{SystemParameters.HighContrast}/{accent}/"
            + $"{SystemColors.HighlightColor}/{SystemColors.WindowColor}/{SystemColors.WindowTextColor}";
    }

}

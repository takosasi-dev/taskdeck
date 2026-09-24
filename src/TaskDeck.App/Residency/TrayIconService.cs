using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Residency;

/// <summary>
/// タスクトレイ（F-091・F-096、UI 設計書 14.3・23.3）。アイコン（件数のバッジ・期限切れの赤・停止中の斜線）とメニューを出す。
/// 件数は DataChangeHub の変更と1分ごとに読み直す（時刻ありのタスクは何も書き込まれなくても期限切れに変わるため）。
/// アイコンは .ico がまだ無いので実行時に描く（タスクバーのライト・ダークで線の色を変える）。描いたアイコンは差し替えたら破棄する。
/// UI スレッドで作って使う。
/// </summary>
internal sealed class TrayIconService(
    TrayViewModel viewModel,
    ITaskRepository tasks,
    DataChangeHub hub,
    ISettingsStore settings,
    ThemeService theme,
    HotkeyService hotkeys,
    ILogger<TrayIconService> logger) : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _image;
    private DispatcherTimer? _timer;
    private Debouncer? _refresh;
    private TrayState _state = new(null, false, "TaskDeck");

    public void Create()
    {
        var icon = new TaskbarIcon { NoLeftClickDelay = true, ContextMenu = new ContextMenu() };
        icon.TrayLeftMouseUp += (_, _) => viewModel.Open();
        // メニューを開く前に作り直す（TrayContextMenuOpen は開いた後に来る）
        icon.PreviewTrayContextMenuOpen += (_, _) => RebuildMenu();
        _icon = icon;
        var refresh = new Debouncer(RefreshAsync);
        _refresh = refresh;
        hub.Changed += OnDataChanged;
        settings.Changed += OnSettingsChanged;
        hotkeys.StatesChanged += OnHotkeysChanged;
        theme.ThemeChanged += OnThemeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => refresh.Schedule(0), icon.Dispatcher);
        _timer.Start();
        Render();
        RebuildMenu();
        refresh.Schedule(0);
        // 作れなかった（Explorer がまだ無い等）ときは、タスクバーができた合図で TaskbarIcon が作り直す
        logger.LogInformation("トレイに常駐しました（アイコン {Size}px、作成済み={Created}）", NativeMethods.SmallIconSize(), icon.IsTaskbarIconCreated);
    }

    public void Dispose()
    {
        hub.Changed -= OnDataChanged;
        settings.Changed -= OnSettingsChanged;
        hotkeys.StatesChanged -= OnHotkeysChanged;
        theme.ThemeChanged -= OnThemeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _timer?.Stop();
        if (_icon is not null)
        {
            _icon.Dispose();
            _icon = null;
            logger.LogInformation("トレイのアイコンを外しました");
        }
        _image?.Dispose();
        _image = null;
    }

    private void OnDataChanged(object? sender, DataChangedEventArgs e)
    {
        if (e.Kinds.HasFlag(DataChangeKind.Tasks))
        {
            Post(() => _refresh?.Schedule(300));
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Post(() => _refresh?.Schedule(0));

    private void OnHotkeysChanged(object? sender, EventArgs e) => RebuildMenu();

    private void OnThemeChanged(object? sender, EventArgs e) => Render();

    /// <summary>タスクバーのライト・ダーク（SystemUsesLightTheme）はアプリのテーマと別に変わる。別スレッドで来る。</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Post(Render);
        }
    }

    private void Post(Action action) => _icon?.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    private async Task RefreshAsync()
    {
        if (_icon is null)
        {
            return;
        }
        var state = viewModel.StateFor(await tasks.GetViewCountsAsync());
        if (state != _state)
        {
            _state = state;
            Render();
        }
    }

    private void Render()
    {
        if (_icon is null)
        {
            return;
        }
        var dark = TrayIconRenderer.IsTaskbarDark();
        var image = TrayIconRenderer.CreateIcon(_state, dark, AccentFor(dark), NativeMethods.SmallIconSize());
        var old = _image;
        _icon.Icon = image;
        _image = image;
        old?.Dispose();
        _icon.ToolTipText = _state.ToolTip;
    }

    /// <summary>バッジのアクセント色（タスクバーの明暗に合わせた段階。設定の固定色 → システム → 既定）。</summary>
    private Color AccentFor(bool dark)
    {
        if (settings.Current.Appearance.AccentColorHex is { Length: > 0 } hex
            && ThemeService.TryParseColor(ThemeService.AccentForTheme(hex, dark)) is { } fixedColor)
        {
            return fixedColor;
        }
        return theme.TryGetSystemAccent(dark) ?? (dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0));
    }

    private void RebuildMenu()
    {
        if (_icon?.ContextMenu is { } menu)
        {
            Fill(menu, viewModel.BuildMenu());
        }
    }

    /// <summary>メニューの中身を並べ直す（トレイと、確認用の書き出しで同じものを使う）。null は区切り線。</summary>
    internal static void Fill(ContextMenu menu, IReadOnlyList<TrayMenuItem?> items)
    {
        menu.Items.Clear();
        foreach (var item in items)
        {
            if (item is null)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var menuItem = new MenuItem { Header = item.Label, InputGestureText = item.Gesture ?? "", Icon = MenuIcon(item.IconKey) };
            var invoke = item.Invoke;
            menuItem.Click += (_, _) => invoke();
            menu.Items.Add(menuItem);
        }
    }

    private static Path? MenuIcon(string key)
    {
        if (Application.Current?.TryFindResource(key) is not Geometry geometry)
        {
            return null;
        }
        var path = new Path
        {
            Data = geometry,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetResourceReference(Shape.StrokeProperty, "TextPrimaryBrush");
        return path;
    }
}

/// <summary>
/// トレイのアイコンを描く（UI 設計書 23.3、形は docs/mockups/Brand.dc.html）。単色のチェックのシルエット（ダークのタスクバーでは白、
/// ライトでは黒）に、右上へ件数のバッジ（アクセント色、期限切れがあれば赤）。通知を止めている間はシルエットを薄くして斜線を重ねる。
/// 形は 16×16 の格子で持ち、アイコンの大きさ（16・20・24・32px など）ごとに画素に合わせて描く
/// （拡大してから描くと、20・24px ではバッジの縁が半端な位置に乗ってにじむため）。
/// </summary>
internal static class TrayIconRenderer
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // UI 設計書 2.2・2.3 の Text.Primary / Text.Tertiary / State.Overdue（ライトとダーク）
    private static readonly Color InkLight = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly Color InkDark = Colors.White;
    private static readonly Color MutedLight = Color.FromRgb(0x6E, 0x6E, 0x6E);
    private static readonly Color MutedDark = Color.FromRgb(0x9B, 0x9B, 0x9B);
    private static readonly Color OverdueLight = Color.FromRgb(0xC4, 0x2B, 0x1C);
    private static readonly Color OverdueDark = Color.FromRgb(0xFF, 0x6B, 0x5E);

    // チェック（16 の格子で M2.6,8.4 L6,11.8 L13.4,4.4、線幅1.7。アプリのアイコンのチェックと同じ、短い脚と長い脚が約1:2.2の形）。
    // 脚はどちらも45°なので、短い脚を直線 y = x + 5.8、長い脚を y = -x + 17.8 と、両端の x で持つ
    private const double CheckStroke = 1.7;
    private const double ShortLeg = 5.8;
    private const double LongLeg = 17.8;
    private const double CheckStart = 2.6;
    private const double CheckEnd = 13.4;

    // 停止中の斜線（直線 y = x の 1.6〜14.4）
    private const double SlashStart = 1.6;
    private const double SlashEnd = 14.4;

    /// <summary>タスクバーがダークか（アプリのテーマとは別の設定。読めなければダーク＝Windows 11 の既定）。</summary>
    public static bool IsTaskbarDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
    }

    public static System.Drawing.Icon CreateIcon(TrayState state, bool darkTaskbar, Color accent, int size)
    {
        using var stream = IconExtensions.ToIconMemoryStream([BitmapFrame.Create(Render(state, darkTaskbar, accent, size))]);
        stream.Position = 0;
        return new System.Drawing.Icon(stream, size, size);
    }

    public static BitmapSource Render(TrayState state, bool darkTaskbar, Color accent, int size)
    {
        var scale = size / 16.0;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var badge = state.Badge;
            var badgeRect = badge is null ? Rect.Empty : BadgeRect(badge.Text, size);
            DrawSilhouette(dc, state.IsPaused, darkTaskbar, badgeRect, size);
            if (badge is not null)
            {
                var fill = badge.IsOverdue ? (darkTaskbar ? OverdueDark : OverdueLight) : accent;
                dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, badgeRect, badgeRect.Height / 2, badgeRect.Height / 2);
                var text = new FormattedText(
                    badge.Text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    (badge.Text.Length == 1 ? 8.0 : 6.6) * scale,
                    new SolidColorBrush(ReadableOn(fill)),
                    1.0);
                dc.DrawText(text, new Point(
                    badgeRect.X + ((badgeRect.Width - text.Width) / 2),
                    badgeRect.Y + ((badgeRect.Height - text.Height) / 2)));
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>チェックの線。バッジがあれば左下へ寄せ、バッジの周りは線を抜いて縁をはっきりさせる。</summary>
    private static void DrawSilhouette(DrawingContext dc, bool paused, bool darkTaskbar, Rect badgeRect, int size)
    {
        var scale = size / 16.0;
        var ink = paused ? (darkTaskbar ? MutedDark : MutedLight) : (darkTaskbar ? InkDark : InkLight);
        var pen = new Pen(new SolidColorBrush(ink), CheckStroke * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var hasBadge = !badgeRect.IsEmpty;
        if (hasBadge)
        {
            var gap = badgeRect;
            gap.Inflate(1.1 * scale, 1.1 * scale);
            dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, size, size)), new RectangleGeometry(gap, gap.Height / 2, gap.Height / 2)));
        }
        // バッジがあれば左下へずらす（停止中は斜線が端から出ないよう左へだけ）
        var (dx, dy) = !hasBadge ? (0.0, 0.0) : paused ? (-0.6, 0.0) : (-0.6, 1.4);
        dc.DrawGeometry(null, pen, Silhouette(scale, paused, dx, dy));
        if (hasBadge)
        {
            dc.Pop();
        }
    }

    /// <summary>
    /// チェック（停止中は斜線も）を 16 の格子から scale 倍した画素の座標で作る。45°の線は、縁が行の中ほどで画素の境目を通ると
    /// 1行が「濃い画素＋ごく薄い縁」になってくっきり見え、画素の中央を通ると半端な濃さの画素が並んでにじむ。
    /// そこで線の太さの端数に合わせて、各直線の位置（y = ±x + b の b）を 0.5 画素の格子に寄せる。
    /// </summary>
    private static StreamGeometry Silhouette(double scale, bool paused, double dx, double dy)
    {
        var half = CheckStroke * scale * Math.Sqrt(2) / 2; // 線を横に切った幅の半分
        var fraction = half - Math.Floor(half);
        var phase = fraction is < 0.25 or > 0.75 ? 0.5 : 0.0;
        double Snap(double b) => Math.Round(b - phase) + phase;
        var shortLeg = Snap((ShortLeg + dy - dx) * scale);
        var longLeg = Snap((LongLeg + dy + dx) * scale);
        var start = (CheckStart + dx) * scale;
        var end = (CheckEnd + dx) * scale;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(start, start + shortLeg), false, false);
            g.LineTo(new Point((longLeg - shortLeg) / 2, (longLeg + shortLeg) / 2), true, true);
            g.LineTo(new Point(end, longLeg - end), true, true);
            if (paused)
            {
                var slash = Snap((dy - dx) * scale);
                g.BeginFigure(new Point((SlashStart + dx) * scale, ((SlashStart + dx) * scale) + slash), false, false);
                g.LineTo(new Point((SlashEnd + dx) * scale, ((SlashEnd + dx) * scale) + slash), true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>右上のバッジ（16 の格子で高さ9。文字数に応じて左へ伸ばす）。縁がにじまないよう画素の境目に乗せる。</summary>
    private static Rect BadgeRect(string text, int size)
    {
        var scale = size / 16.0;
        var width = Math.Round(text.Length switch { 1 => 9, 2 => 11.5, _ => 14 } * scale);
        var height = Math.Round(9 * scale);
        return new Rect(size - width, 0, width, height);
    }

    /// <summary>地の色の上で読める文字色（白か黒の、コントラストの高い方）。</summary>
    private static Color ReadableOn(Color fill)
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        var luminance = (0.2126 * Channel(fill.R)) + (0.7152 * Channel(fill.G)) + (0.0722 * Channel(fill.B));
        return (luminance + 0.05) / 0.05 > 1.05 / (luminance + 0.05) ? Colors.Black : Colors.White;
    }
}

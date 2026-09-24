using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Serilog;
using TaskDeck.App.Residency;

namespace TaskDeck.App.Views.Startup;

/// <summary>
/// 起動画面（S-16、F-187、UI 設計書 23.4、モックアップ Brand.dc.html）。480×320・角丸12・地 #14161A。
/// WPF の窓にはせず、専用のスレッドが GDI+ で描いて重ね合わせ窓（layered window）に出す。UI スレッドがメイン画面を組み立てている間も
/// 動きが止まらないので、その間に DB の準備とメイン画面の組み立てを並べられる（NFR 3.2 の起動2秒。設計メモの変えた点 #24）。
/// 動きは 500ms で終わり、要素ごとに独立させる（連鎖させない）: 読み込みが速いと途中で閉じるので、どこで切れても不自然に見えないように。
/// 0–150ms アイコン（不透明度 0→1・拡大 0.94→1）／100–400ms チェックを線で描く／250–400ms ロゴ（下から 4px＋フェード）／
/// 350–450ms タグライン（フェード）／ずっと 進捗バーが流れる。動きを減らす設定なら何も動かさず、帯も出さない。
/// </summary>
internal sealed class StartupSplash
{
    public const int Width = 480;
    public const int Height = 320;

    /// <summary>動きがすべて終わる時刻（ms）。</summary>
    public const double SettledMs = 450;

    private const double FadeOutMs = 100;
    private const int FrameMs = 15;

    private readonly bool _reduceMotion;
    private readonly bool _highContrast;
    private volatile bool _closeRequested;

    private StartupSplash(bool reduceMotion, bool highContrast)
    {
        _reduceMotion = reduceMotion;
        _highContrast = highContrast;
    }

    /// <summary>起動画面を出す（すぐ返る。描くのは専用のスレッド）。</summary>
    /// <param name="reduceMotion">動きを省く（設定の「動きを減らす」、未設定なら OS の設定）。</param>
    /// <param name="highContrast">OS がハイコントラスト（OS の色で描く）。</param>
    public static StartupSplash Show(bool reduceMotion, bool highContrast)
    {
        var splash = new StartupSplash(reduceMotion, highContrast);
        new Thread(splash.Run) { IsBackground = true, Name = "TaskDeck 起動画面" }.Start();
        return splash;
    }

    /// <summary>100ms で消えて閉じる（動きを減らす設定ならすぐ閉じる）。どのスレッドから何度呼んでもよい。</summary>
    public void Close() => _closeRequested = true;

    /// <summary>1枚を PNG に書き出す（確認用の入口から）。ms が null なら動きを減らす設定の形。</summary>
    public static void SaveFrame(string file, double? ms, double scale, bool highContrast)
    {
        using var bitmap = new Bitmap((int)Math.Round(Width * scale), (int)Math.Round(Height * scale), PixelFormat.Format32bppPArgb);
        using (var art = new Art(scale, highContrast ? Palette.FromSystemColors() : Palette.Dark))
        {
            art.Draw(bitmap, ms);
        }
        bitmap.Save(file, ImageFormat.Png);
    }

    private void Run()
    {
#pragma warning disable CA1031 // 起動画面は無くても起動は続く。描けなかったらログに残して何も出さない（このスレッドの例外はアプリを落とすので必ず受ける）
        try
        {
            // マウスのあるモニタの作業領域の中央（起動したアイコンのそば）。拡大率はそのモニタのもの
            var monitor = NativeMethods.CursorMonitor();
            var scale = monitor?.Scale ?? 1.0;
            using var surface = new Surface((int)Math.Round(Width * scale), (int)Math.Round(Height * scale));
            using var art = new Art(scale, _highContrast ? Palette.FromSystemColors() : Palette.Dark);
            var work = monitor?.Work ?? default;
            var origin = new Native.Point
            {
                X = work.Left + ((work.Width - surface.Image.Width) / 2),
                Y = work.Top + ((work.Height - surface.Image.Height) / 2),
            };
            var window = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                "Static", "TaskDeck", Native.WS_POPUP, origin.X, origin.Y, surface.Image.Width, surface.Image.Height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero)
            {
                Log.Warning("起動画面の窓を作れませんでした（{Error}）", Marshal.GetLastPInvokeError());
                return;
            }
            try
            {
                Play(window, surface, art, origin);
            }
            finally
            {
                Native.DestroyWindow(window);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "起動画面を描けませんでした");
        }
#pragma warning restore CA1031
    }

    /// <summary>閉じるよう言われるまで描き続け、言われたら 100ms で消す。動きの時刻は最初の1枚を出したときから数える。</summary>
    private void Play(IntPtr window, Surface surface, Art art, Native.Point origin)
    {
        var clock = Stopwatch.StartNew();
        double? closingAt = null;
        var shown = false;
        while (true)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (closingAt is null && _closeRequested)
            {
                if (_reduceMotion)
                {
                    return;
                }
                closingAt = now;
            }
            var opacity = closingAt is { } from ? 1 - ((now - from) / FadeOutMs) : 1;
            if (opacity <= 0)
            {
                return;
            }
            if (!_reduceMotion || !shown)
            {
                art.Draw(surface.Image, _reduceMotion ? null : now);
                surface.Present(window, origin, (byte)Math.Round(opacity * 255));
            }
            if (!shown)
            {
                Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);
                shown = true;
            }
            while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
            Thread.Sleep(FrameMs);
        }
    }

    /// <summary>色（地は暗い固定色。ハイコントラストだけ OS の色）。</summary>
    private sealed record Palette(
        Color Background, Color Logo, Color Tagline, Color Version, Color Track, Color Band,
        Color IconBack, Color IconMiddle, Color IconFront, Color Check)
    {
        /// <summary>モックアップの色。アイコンは暗い地用: 背面2枚は白 14%・26%、前面は #0067C0 に白 7% を重ねた色。</summary>
        public static readonly Palette Dark = new(
            Background: Color.FromArgb(0x14, 0x16, 0x1A),
            Logo: Color.White,
            Tagline: Color.FromArgb(0x7A, 0x80, 0x89),
            Version: Color.FromArgb(0x4A, 0x4F, 0x57),
            Track: Color.FromArgb(0x22, 0x26, 0x2C),
            Band: Color.FromArgb(0x00, 0x67, 0xC0),
            IconBack: Color.FromArgb(36, 255, 255, 255),
            IconMiddle: Color.FromArgb(66, 255, 255, 255),
            IconFront: Color.FromArgb(0x12, 0x72, 0xC4),
            Check: Color.White);

        /// <summary>ハイコントラストでは OS の色で描く（固定の暗い色のままだと、白地のテーマで文字が読めない）。</summary>
        public static Palette FromSystemColors() => new(
            Background: SystemColors.Window,
            Logo: SystemColors.WindowText,
            Tagline: SystemColors.WindowText,
            Version: SystemColors.GrayText,
            Track: SystemColors.GrayText,
            Band: SystemColors.Highlight,
            IconBack: Color.FromArgb(36, SystemColors.WindowText),
            IconMiddle: Color.FromArgb(66, SystemColors.WindowText),
            IconFront: SystemColors.Highlight,
            Check: SystemColors.HighlightText);
    }

    /// <summary>1枚分の絵。座標は 480×320 の論理ピクセル（拡大率は描くときに掛ける）。</summary>
    private sealed class Art : IDisposable
    {
        private const string Logo = "TaskDeck";

        /// <summary>タグライン 12px。字送り +0.04em（0.48px）は GDI+ に指定が無いので、1文字ずつ並べて右を空ける。</summary>
        private const string Tagline = "思いついた速さで、片付ける";
        private const float LetterSpacing = 0.48f;

        // アイコン 76px・上から78px（Resources/Brand/AppIcon.xaml の 256px の座標が正本）
        private const float IconSize = 76;
        private const float IconTop = 78;
        private const float CheckThickness = 24;
        private const float BandWidth = 163;
        private static readonly PointF[] Check = [new(78, 152), new(110, 184), new(178, 108)];
        private static readonly float CheckLength = Distance(Check[0], Check[1]) + Distance(Check[1], Check[2]);

        private readonly float _scale;
        private readonly Palette _palette;
        private readonly string _version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        private readonly StringFormat _format = (StringFormat)StringFormat.GenericTypographic.Clone();
        private readonly Font _logoFont = new("Segoe UI Semibold", 26, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font _taglineFont = new("Yu Gothic UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font _versionFont = new("Consolas", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly GraphicsPath _card = RoundedRect(0, 0, Width, Height, 12);
        private readonly GraphicsPath _backCard = RoundedRect(56, 26, 144, 42, 17);
        private readonly GraphicsPath _middleCard = RoundedRect(41, 48, 174, 42, 19);
        private readonly GraphicsPath _frontCard = RoundedRect(25, 72, 206, 158, 46);
        private readonly float _logoWidth;
        private readonly float[] _taglineAdvances;
        private readonly float _taglineWidth;
        private readonly float _versionWidth;
        private readonly float _versionHeight;

        public Art(double scale, Palette palette)
        {
            _scale = (float)scale;
            _palette = palette;
            using var probe = new Bitmap(1, 1);
            using var g = Graphics.FromImage(probe);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            _logoWidth = Measure(g, Logo, _logoFont);
            _taglineAdvances = [.. Tagline.Select(c => Measure(g, c.ToString(), _taglineFont))];
            _taglineWidth = _taglineAdvances.Sum() + (LetterSpacing * Tagline.Length);
            _versionWidth = Measure(g, _version, _versionFont);
            _versionHeight = _versionFont.GetHeight(g);
        }

        /// <param name="ms">動き始めからの時刻。null なら動きを減らす設定の形（動き終わりの形で、進捗の帯を出さない）。</param>
        public void Draw(Bitmap target, double? ms)
        {
            using var g = Graphics.FromImage(target);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // 重ね合わせはガンマ補正をしない（WPF と同じ。HighQuality だと半透明の白が明るく出る）
            g.CompositingQuality = CompositingQuality.AssumeLinear;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.ScaleTransform(_scale, _scale);
            var t = ms ?? SettledMs;

            using (var background = new SolidBrush(_palette.Background))
            {
                g.FillPath(background, _card);
            }
            DrawIcon(g, t);

            // ロゴ 26px SemiBold（上から186）: 250–400ms で下から 4px 上がりながら出る
            var logo = CubicOut(Progress(t, 250, 400));
            DrawText(g, Logo, _logoFont, _palette.Logo, logo, (Width - _logoWidth) / 2, 186 + (4 * (1 - logo)));

            // タグライン（上から222）: 350–450ms で出る
            var tagline = CubicOut(Progress(t, 350, 450));
            var x = (Width - _taglineWidth) / 2;
            for (var i = 0; i < Tagline.Length; i++)
            {
                DrawText(g, Tagline[i].ToString(), _taglineFont, _palette.Tagline, tagline, x, 222);
                x += _taglineAdvances[i] + LetterSpacing;
            }

            DrawProgress(g, ms);

            // バージョン 右下 10px（右から14・下から12）
            DrawText(g, _version, _versionFont, _palette.Version, 1, Width - 14 - _versionWidth, Height - 12 - _versionHeight);
        }

        public void Dispose()
        {
            _format.Dispose();
            _logoFont.Dispose();
            _taglineFont.Dispose();
            _versionFont.Dispose();
            _card.Dispose();
            _backCard.Dispose();
            _middleCard.Dispose();
            _frontCard.Dispose();
        }

        /// <summary>アイコン: 0–150ms で出て（拡大 0.94→1）、100–400ms でチェックを線で描く。</summary>
        private void DrawIcon(Graphics g, double t)
        {
            var appear = CubicOut(Progress(t, 0, 150));
            if (appear <= 0)
            {
                return;
            }
            var size = (float)(0.94 + (0.06 * appear)) * IconSize;
            var state = g.Save();
            g.TranslateTransform(Width / 2f, IconTop + (IconSize / 2));
            g.ScaleTransform(size / 256, size / 256);
            g.TranslateTransform(-128, -128);
            Fill(g, _backCard, _palette.IconBack, appear);
            Fill(g, _middleCard, _palette.IconMiddle, appear);
            Fill(g, _frontCard, _palette.IconFront, appear);

            // チェックはペンで引くように、始めと終わりをゆっくり描き足す。
            // 線の始まりに丸い端が点で残らないよう、始めは線の太さの 1/4 だけ手前から描き始める
            var drawn = (SineInOut(Progress(t, 100, 400)) * (CheckLength + (CheckThickness / 4))) - (CheckThickness / 4);
            if (drawn > 0)
            {
                using var pen = new Pen(Fade(_palette.Check, appear), CheckThickness)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                g.DrawLines(pen, CheckUpTo(drawn));
            }
            g.Restore(state);
        }

        /// <summary>進捗バー: 下端 2px。帯（幅 163）が左の外から右の外へ 1.2 秒で流れ、閉じるまで繰り返す（動きを減らす設定では帯を出さない）。</summary>
        private void DrawProgress(Graphics g, double? ms)
        {
            g.SetClip(_card);
            using (var track = new SolidBrush(_palette.Track))
            {
                g.FillRectangle(track, 0, Height - 2, Width, 2);
            }
            if (ms is { } t)
            {
                var x = -BandWidth + ((Width + BandWidth) * (float)SineInOut(t % 1200 / 1200));
                using var band = new SolidBrush(_palette.Band);
                g.FillRectangle(band, x, Height - 2, BandWidth, 2);
            }
            g.ResetClip();
        }

        private void DrawText(Graphics g, string text, Font font, Color color, double opacity, float x, double y)
        {
            if (opacity <= 0 || text.Length == 0)
            {
                return;
            }
            using var brush = new SolidBrush(Fade(color, opacity));
            g.DrawString(text, font, brush, x, (float)y, _format);
        }

        private float Measure(Graphics g, string text, Font font) => g.MeasureString(text, font, PointF.Empty, _format).Width;

        private static void Fill(Graphics g, GraphicsPath path, Color color, double opacity)
        {
            using var brush = new SolidBrush(Fade(color, opacity));
            g.FillPath(brush, path);
        }

        /// <summary>チェックの折れ線を、始めから length の長さまで。</summary>
        private static PointF[] CheckUpTo(double length)
        {
            var points = new List<PointF> { Check[0] };
            for (var i = 1; i < Check.Length && length > 0; i++)
            {
                var segment = Distance(Check[i - 1], Check[i]);
                var f = (float)Math.Min(1, length / segment);
                points.Add(new PointF(Check[i - 1].X + ((Check[i].X - Check[i - 1].X) * f), Check[i - 1].Y + ((Check[i].Y - Check[i - 1].Y) * f)));
                length -= segment;
            }
            return [.. points];
        }

        private static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Fade(Color color, double opacity) => Color.FromArgb((int)Math.Round(color.A * opacity), color);

        private static float Distance(PointF a, PointF b) => MathF.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

        private static double Progress(double t, double from, double to) => Math.Clamp((t - from) / (to - from), 0, 1);

        /// <summary>WPF の CubicEase（EaseOut）。UI 設計書の EaseStandard。</summary>
        private static double CubicOut(double p) => 1 - Math.Pow(1 - p, 3);

        /// <summary>WPF の SineEase（EaseInOut）。</summary>
        private static double SineInOut(double p) => (1 - Math.Cos(Math.PI * p)) / 2;
    }

    /// <summary>描き先（32bit の DIB。GDI+ はここへ直接描き、そのまま重ね合わせ窓に渡す）。</summary>
    private sealed class Surface : IDisposable
    {
        private readonly IntPtr _dc;
        private readonly IntPtr _bitmap;
        private readonly IntPtr _previous;

        public Surface(int width, int height)
        {
            var header = new Native.BitmapInfoHeader
            {
                Size = Marshal.SizeOf<Native.BitmapInfoHeader>(),
                Width = width,
                Height = -height, // 上から下へ（GDI+ の Bitmap と同じ並び）
                Planes = 1,
                BitCount = 32,
            };
            _dc = Native.CreateCompatibleDC(IntPtr.Zero);
            _bitmap = Native.CreateDIBSection(_dc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (_dc == IntPtr.Zero || _bitmap == IntPtr.Zero)
            {
                Native.DeleteDC(_dc);
                throw new ExternalException("起動画面の描き先を作れませんでした");
            }
            _previous = Native.SelectObject(_dc, _bitmap);
            Image = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        public Bitmap Image { get; }

        public void Present(IntPtr window, Native.Point origin, byte opacity)
        {
            var size = new Native.Size { Width = Image.Width, Height = Image.Height };
            var source = default(Native.Point);
            var blend = new Native.BlendFunction { SourceConstantAlpha = opacity, AlphaFormat = Native.AC_SRC_ALPHA };
            Native.UpdateLayeredWindow(window, IntPtr.Zero, ref origin, ref size, _dc, ref source, 0, ref blend, Native.ULW_ALPHA);
        }

        public void Dispose()
        {
            Image.Dispose();
            Native.SelectObject(_dc, _previous);
            Native.DeleteObject(_bitmap);
            Native.DeleteDC(_dc);
        }
    }

    /// <summary>重ね合わせ窓（layered window）まわりの Win32。戻り値は呼ぶ側で確かめる。</summary>
    private static class Native
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_ALPHA = 1;
        public const uint PM_REMOVE = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Size
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Message
        {
            public IntPtr Window;
            public uint Id;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public Point Cursor;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
        public static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(
            IntPtr window, IntPtr destination, ref Point origin, ref Size size, IntPtr source, ref Point sourceOrigin,
            int colorKey, ref BlendFunction blend, int flags);

        [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(out Message message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        public static extern IntPtr DispatchMessage(ref Message message);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr item);
    }
}

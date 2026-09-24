using System.Globalization;
using System.Xml.Linq;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>
/// 色の計算（tools/validate-palette.js と同じ式: WCAG 2 の相対輝度とコントラスト比、OKLab）。
/// テストの中で「値を変えたのに検証をかけ忘れた」を拾うための最小限の写し。
/// </summary>
internal static class ColorMath
{
    public static (double R, double G, double B) Parse(string hex)
    {
        var body = hex.TrimStart('#');
        if (body.Length == 8)
        {
            body = body[2..]; // #AARRGGBB（XAML）
        }
        double Channel(int start) => int.Parse(body.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return (Channel(0), Channel(2), Channel(4));
    }

    public static double Contrast(string foreground, string background)
    {
        var a = Luminance(Parse(foreground));
        var b = Luminance(Parse(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>OKLab の (L, a, b)。</summary>
    public static (double L, double A, double B) OkLab(string hex)
    {
        var (r, g, b) = Parse(hex);
        var (lr, lg, lb) = (Linear(r), Linear(g), Linear(b));
        var l = Math.Cbrt((0.4122214708 * lr) + (0.5363325363 * lg) + (0.0514459929 * lb));
        var m = Math.Cbrt((0.2119034982 * lr) + (0.6806995451 * lg) + (0.1073969566 * lb));
        var s = Math.Cbrt((0.0883024619 * lr) + (0.2817188376 * lg) + (0.6299787005 * lb));
        return (
            (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    /// <summary>OKLCh の色相（度）。</summary>
    public static double Hue(string hex)
    {
        var (_, a, b) = OkLab(hex);
        var h = Math.Atan2(b, a) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>色相の差（円周上の近い方、0〜180）。</summary>
    public static double HueGap(double x, double y) => Math.Abs(((x - y + 540) % 360) - 180);

    private static double Luminance((double R, double G, double B) rgb) =>
        (0.2126 * Linear(rgb.R)) + (0.7152 * Linear(rgb.G)) + (0.0722 * Linear(rgb.B));

    private static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
}

/// <summary>リポジトリの中のファイル（テーマ辞書など）を読む。</summary>
internal static class RepoFiles
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static string Root { get; } = FindRoot();

    public static string ThemePath(string fileName) =>
        Path.Combine(Root, "src", "TaskDeck.App", "Resources", "Themes", fileName);

    public static string StylePath(string fileName) =>
        Path.Combine(Root, "src", "TaskDeck.App", "Resources", "Styles", fileName);

    /// <summary>XAML の辞書の x:Key を並び順に。</summary>
    public static IReadOnlyList<string> Keys(string path) =>
        [.. XDocument.Load(path).Root!.Elements()
            .Select(e => (string?)e.Attribute(Xaml + "Key"))
            .OfType<string>()];

    /// <summary>テーマ辞書の &lt;Color x:Key="..."&gt;#AARRGGBB&lt;/Color&gt; を読む（x:Static のものは含まない）。</summary>
    public static IReadOnlyDictionary<string, string> Colors(string path) =>
        XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "Color" && e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value.Trim());

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TaskDeck.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("TaskDeck.slnx のあるフォルダが見つかりません。");
    }
}

using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskDeck.Tools.IconGen;

/// <summary>
/// アプリのアイコン（UI 設計書 23.2）を書き出す。形は src/TaskDeck.App/Resources/Brand/AppIcon.xaml の大きさごとの絵。
/// `dotnet run --project tools/IconGen` で src/TaskDeck.App/Assets/ の TaskDeck.ico（256/128/48/32/16）と
/// 大きさごとの PNG（icon-256.png など。README 用）を作り直す。
/// </summary>
internal static class Program
{
    /// <summary>書き出す大きさと、その大きさ用の絵のキー（.ico にもこの順に入れる）。</summary>
    private static readonly (int Size, string Key)[] Sizes =
    [
        (256, "AppIconImage"),
        (128, "AppIcon128Image"),
        (48, "AppIcon48Image"),
        (32, "AppIcon32Image"),
        (16, "AppIcon16Image"),
    ];

    [STAThread]
    private static void Main()
    {
        var app = Path.Combine(RepositoryRoot(), "src", "TaskDeck.App");
        ResourceDictionary brand;
        using (var xaml = File.OpenRead(Path.Combine(app, "Resources", "Brand", "AppIcon.xaml")))
        {
            brand = (ResourceDictionary)XamlReader.Load(xaml);
        }
        var assets = Directory.CreateDirectory(Path.Combine(app, "Assets")).FullName;
        var images = new List<(int Size, byte[] Png)>();
        foreach (var (size, key) in Sizes)
        {
            var png = Png(Render((ImageSource)brand[key], size));
            File.WriteAllBytes(Path.Combine(assets, $"icon-{size}.png"), png);
            images.Add((size, png));
        }
        File.WriteAllBytes(Path.Combine(assets, "TaskDeck.ico"), Ico(images));
        Console.WriteLine($"{assets} に TaskDeck.ico と PNG {images.Count} 枚を書き出しました");
    }

    /// <summary>このファイル（tools/IconGen/Program.cs）から見たリポジトリの直下。</summary>
    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    /// <summary>絵を size×size の枠いっぱいに描く（大きさごとの絵はその大きさの座標なので等倍になる）。</summary>
    private static RenderTargetBitmap Render(ImageSource image, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, size, size));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// ICO（ICONDIR＋大きさごとの ICONDIRENTRY＋画像）。画像は 32 ビットの PNG のまま入れる（Windows Vista 以降のシェル・WPF・
    /// System.Drawing・コンパイラの ApplicationIcon のどれも読める）。幅・高さの 256 は 0 と書く決まり。
    /// </summary>
    private static byte[] Ico(List<(int Size, byte[] Png)> images)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0); // 予約
            writer.Write((ushort)1); // 種類（1 = アイコン）
            writer.Write((ushort)images.Count);
            var offset = 6 + (16 * images.Count);
            foreach (var (size, png) in images)
            {
                writer.Write((byte)(size % 256)); // 幅（256 は 0）
                writer.Write((byte)(size % 256)); // 高さ
                writer.Write((byte)0); // パレットの色数（使わない）
                writer.Write((byte)0); // 予約
                writer.Write((ushort)1); // 面の数
                writer.Write((ushort)32); // 1画素のビット数
                writer.Write(png.Length);
                writer.Write(offset);
                offset += png.Length;
            }
            foreach (var (_, png) in images)
            {
                writer.Write(png);
            }
        }
        return stream.ToArray();
    }
}

using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace TaskDeck.Tests.Brand;

/// <summary>アプリのアイコン src/TaskDeck.App/Assets/TaskDeck.ico（tools/IconGen が書き出す。UI 設計書 23.2）。</summary>
public class AppIconTests
{
    [Fact]
    public void TaskDeckIco_BitmapDecoder_ReadsFiveSizesWithContent()
    {
        using var stream = File.OpenRead(IcoPath());
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        Assert.Equal([16, 32, 48, 128, 256], decoder.Frames.Select(f => f.PixelWidth).Order());
        Assert.All(decoder.Frames, f => Assert.Equal(f.PixelWidth, f.PixelHeight));
        Assert.All(decoder.Frames, f => Assert.Contains(Alphas(f), a => a == 255));
    }

    /// <summary>
    /// 256 は ICO の決まりで幅・高さを 0 と書くが、System.Drawing.Icon の大きさ選びは 0 を 256 と読まず、256 を頼んでも 128 を返す
    /// （System.Drawing 側の制約。ICO の形式では直せない）。256 は上の BitmapDecoder（WPF の窓のアイコンと同じ読み方）で確かめる。
    /// </summary>
    [Theory]
    [InlineData(128)]
    [InlineData(48)]
    [InlineData(32)]
    [InlineData(16)]
    public void TaskDeckIco_SystemDrawingIcon_ReadsEachSize(int size)
    {
        using var icon = new System.Drawing.Icon(IcoPath(), size, size);
        using var bitmap = icon.ToBitmap();

        Assert.Equal(size, icon.Width);
        Assert.Equal(size, bitmap.Width);
        Assert.Equal(255, bitmap.GetPixel(size / 2, size * 3 / 4).A); // 最前面のカードの中
    }

    private static string IcoPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src", "TaskDeck.App", "Assets", "TaskDeck.ico"));

    private static IEnumerable<byte> Alphas(BitmapSource frame)
    {
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return pixels.Where((_, i) => i % 4 == 3);
    }
}

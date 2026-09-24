using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskDeck.App.Residency;

namespace TaskDeck.Tests.Brand;

/// <summary>トレイのアイコンの絵（UI 設計書 23.3）。高 DPI の大きさ（20・24・32px）でも同じように描けて、にじまないこと。</summary>
public class TrayIconRendererTests
{
    private static readonly Color Accent = Color.FromRgb(0x00, 0x67, 0xC0);

    private static readonly TrayState[] States =
    [
        new(null, false, ""),
        new(new TrayBadge(7, false), false, ""),
        new(new TrayBadge(3, true), false, ""),
        new(new TrayBadge(12, false), false, ""),
        new(new TrayBadge(120, true), false, ""),
        new(new TrayBadge(5, false), true, ""),
        new(null, true, ""),
    ];

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Render_EveryStateOnBothTaskbars_DrawsIconOfThatSize(int size)
    {
        foreach (var state in States)
        {
            foreach (var dark in new[] { false, true })
            {
                var bitmap = TrayIconRenderer.Render(state, dark, Accent, size);

                Assert.Equal(size, bitmap.PixelWidth);
                Assert.Equal(size, bitmap.PixelHeight);
                Assert.Contains(Alphas(bitmap), a => a > 0);
            }
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Render_TwoDigitBadge_BadgeEdgesOnPixelBoundaries(int size)
    {
        var bitmap = TrayIconRenderer.Render(new TrayState(new TrayBadge(12, false), false, ""), false, Accent, size);
        var alphas = Alphas(bitmap).ToArray();
        // バッジは 16 の格子で高さ9・幅11.5 の角丸（上端は0）。真ん中の列は、上から高さぶんが塗りつぶし（数字を重ねた画素は
        // 合成の丸めで 254 になる）で、その下はチェックを抜いた隙間。縁が半画素にずれると境目の行が半透明になる
        var height = (int)Math.Round(9 * size / 16.0);
        var column = size - ((int)Math.Round(11.5 * size / 16.0) / 2);

        Assert.All(Enumerable.Range(0, height), y => Assert.InRange(alphas[(y * size) + column], (byte)250, (byte)255));
        Assert.Equal(0, alphas[(height * size) + column]);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void CreateIcon_OverdueBadge_ReturnsIconOfThatSize(int size)
    {
        using var icon = TrayIconRenderer.CreateIcon(new TrayState(new TrayBadge(120, true), false, ""), true, Accent, size);

        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }

    private static IEnumerable<byte> Alphas(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Where((_, i) => i % 4 == 3);
    }
}

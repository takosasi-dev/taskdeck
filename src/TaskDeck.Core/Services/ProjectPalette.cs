namespace TaskDeck.Core.Services;

/// <summary>
/// プロジェクト色（UI 設計書 2.5）。ライトの6色は色覚検証を通した値で、順序にも意味がある（隣り合う色の差を保つ並び）。
/// 新規プロジェクトには未使用の色をこの順で割り当てる。7件目以降は先頭から繰り返す。
/// </summary>
public static class ProjectPalette
{
    public static IReadOnlyList<string> Light { get; } =
    [
        "#0067C0", // 青
        "#CA5010", // 橙
        "#8764B8", // 紫
        "#0E7C5A", // 緑（ティール寄り）
        "#C2417A", // 赤紫
        "#8A6D00", // 黄
    ];

    /// <summary>
    /// ダークテーマでの対応色（Light と同じ順・同じ役割）。地は Surface.Content(dark) の #272727。
    ///
    /// node tools/validate-palette.js "#4CC2FF,#FF8B56,#A981E1,#80DDB6,#E05691,#EDCE78" --mode dark の結果
    /// （2026-09-23 合格、tools/palette-results.md に全表）:
    /// 隣接ΔE の最小は 正常色覚 24.4／色覚特性 16.5、彩度は 0.106〜0.181、地とのコントラストは 4.20〜9.75。
    /// ライトの明度を一律に上げると緑と赤紫が2型色覚で ΔE 1.8 まで詰まる（open_issues 2.1）ため、
    /// 色相は保ったまま明度を色ごとにずらして分けた（緑 L=0.83 / 赤紫 L=0.65）。
    /// 青はダークのアクセント（#4CC2FF）と同じ値（ライトの青もアクセントと同じ #0067C0 なので、その関係を保つ）。
    /// 値を変えたら必ず再検証する（役割と順は tests/…/Appearance/ProjectPaletteTests も確かめる）。
    /// </summary>
    public static IReadOnlyList<string> Dark { get; } =
    [
        "#4CC2FF", // 青（Accent.Default(dark) と同じ）
        "#FF8B56", // 橙
        "#A981E1", // 紫
        "#80DDB6", // 緑（ティール寄り）
        "#E05691", // 赤紫
        "#EDCE78", // 黄
    ];

    /// <summary>使われていない色をパレット順で返す。全部使われていれば、使用数が最も少ない色（同数なら順番の早い方）。</summary>
    public static string NextColor(IEnumerable<string> usedColorHexes)
    {
        var counts = Light.ToDictionary(c => c, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var hex in usedColorHexes)
        {
            if (counts.TryGetValue(hex, out var n))
            {
                counts[hex] = n + 1;
            }
        }
        return Light.OrderBy(c => counts[c]).ThenBy(c => IndexOf(c)).First();
    }

    /// <summary>テーマに合わせた表示色。パレット外の色（ユーザーが自由に選んだ色）はそのまま返す。</summary>
    public static string ForTheme(string colorHex, bool dark)
    {
        if (!dark)
        {
            return colorHex;
        }
        var i = IndexOf(colorHex);
        return i >= 0 ? Dark[i] : colorHex;
    }

    private static int IndexOf(string colorHex)
    {
        for (var i = 0; i < Light.Count; i++)
        {
            if (string.Equals(Light[i], colorHex, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
}

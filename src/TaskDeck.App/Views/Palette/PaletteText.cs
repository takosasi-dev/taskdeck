namespace TaskDeck.App.Views.Palette;

/// <summary>強調して描く文字の切れ端（IsMatch=true は一致した部分）。</summary>
public sealed record TextPiece(string Text, bool IsMatch);

/// <summary>名前と検索語の一致。Strength は 0（不一致）〜3（前方一致）。Spans は名前の中の一致した位置（強調に使う）。</summary>
public sealed record PaletteMatch(int Strength, IReadOnlyList<(int Start, int Length)> Spans)
{
    public static PaletteMatch None { get; } = new(0, []);

    public bool IsMatch => Strength > 0;
}

/// <summary>
/// パレットの文字の比べ方（UI 設計書 11.4）。強さは 前方一致(3) ＞ 部分一致(2) ＞ あいまい一致(1: 語の文字が順に含まれる)。
/// 語が複数あれば全部が当たったものだけを出し（AND）、強さは一番弱い語のもの。
/// 比べる前に1文字ずつ折りたたむ（全角英数→半角・カタカナ→ひらがな・大文字→小文字）。
/// 長さが変わらないので、一致の位置をそのまま元の文字列の強調に使える。
/// </summary>
public static class PaletteText
{
    public const int Loose = 1;
    public const int Contains = 2;
    public const int Prefix = 3;

    public static char Fold(char c) => c switch
    {
        >= '！' and <= '～' => char.ToLowerInvariant((char)(c - 0xFEE0)),
        '　' or '\t' or '\r' or '\n' => ' ',
        >= 'ァ' and <= 'ヶ' => (char)(c - 0x60),
        _ => char.ToLowerInvariant(c),
    };

    public static string Fold(string? text) => string.IsNullOrEmpty(text) ? "" : string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = Fold(source[i]);
        }
    });

    /// <summary>空白区切りの検索語（折りたたみ済み）。</summary>
    public static IReadOnlyList<string> Terms(string? query) => Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 名前（コマンド・プロジェクト・タグ・ビュー・テンプレート）との一致。語が無ければ全部に当たる（強さはどれも部分一致）。
    /// keywords（読み・別名）に当たったものは部分一致として扱い、名前は強調しない。
    /// </summary>
    public static PaletteMatch Match(string name, IReadOnlyList<string> terms, string? keywords = null)
    {
        if (terms.Count == 0)
        {
            return new PaletteMatch(Contains, []);
        }
        var folded = Fold(name);
        var foldedKeywords = Fold(keywords);
        var strength = Prefix;
        var spans = new List<(int, int)>();
        foreach (var term in terms)
        {
            var (termStrength, termSpans) = MatchTerm(folded, term);
            if (termStrength < Contains && foldedKeywords.Contains(term, StringComparison.Ordinal))
            {
                (termStrength, termSpans) = (Contains, []);
            }
            if (termStrength == 0)
            {
                return PaletteMatch.None;
            }
            strength = Math.Min(strength, termStrength);
            spans.AddRange(termSpans);
        }
        return new PaletteMatch(strength, spans);
    }

    /// <summary>
    /// タスクのタイトルとの一致（タスクは DB の検索で絞り込み済み）。前方一致(3) ＞ 部分一致(2) ＞ タイトルに無い語がある(1: メモに当たった)。
    /// あいまい一致は使わない（DB がタイトルとメモの部分一致で選んでいるため）。
    /// </summary>
    public static PaletteMatch MatchTitle(string title, IReadOnlyList<string> terms)
    {
        var folded = Fold(title);
        var strength = Prefix;
        var spans = new List<(int, int)>();
        foreach (var term in terms)
        {
            var at = folded.IndexOf(term, StringComparison.Ordinal);
            if (at < 0)
            {
                strength = Loose;
                continue;
            }
            strength = Math.Min(strength, at == 0 ? Prefix : Contains);
            spans.Add((at, term.Length));
        }
        return new PaletteMatch(terms.Count == 0 ? Contains : strength, spans);
    }

    /// <summary>text を一致した部分とそれ以外に切り分ける。spans は text の offset 文字目から数えた位置。</summary>
    public static IReadOnlyList<TextPiece> Pieces(string text, IEnumerable<(int Start, int Length)> spans, int offset = 0)
    {
        if (text.Length == 0)
        {
            return [];
        }
        var marks = new bool[text.Length];
        foreach (var (start, length) in spans)
        {
            for (var i = Math.Max(0, start + offset); i < Math.Min(text.Length, start + offset + length); i++)
            {
                marks[i] = true;
            }
        }
        var pieces = new List<TextPiece>();
        var runStart = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || marks[i] != marks[runStart])
            {
                pieces.Add(new TextPiece(text[runStart..i], marks[runStart]));
                runStart = i;
            }
        }
        return pieces;
    }

    /// <summary>語の1つを名前（折りたたみ済み）に当てる。前方 → 部分 → あいまい（文字が順に現れる。続いた文字は1つの強調にまとめる）。</summary>
    private static (int Strength, List<(int, int)> Spans) MatchTerm(string name, string term)
    {
        var at = name.IndexOf(term, StringComparison.Ordinal);
        if (at >= 0)
        {
            return (at == 0 ? Prefix : Contains, [(at, term.Length)]);
        }
        var spans = new List<(int Start, int Length)>();
        var from = 0;
        foreach (var ch in term)
        {
            var i = name.IndexOf(ch, from);
            if (i < 0)
            {
                return (0, []);
            }
            if (spans.Count > 0 && spans[^1].Start + spans[^1].Length == i)
            {
                spans[^1] = (spans[^1].Start, spans[^1].Length + 1);
            }
            else
            {
                spans.Add((i, 1));
            }
            from = i + 1;
        }
        return (Loose, spans);
    }
}

/// <summary>絞り込み記号（UI 設計書 11.3）。</summary>
public enum PaletteMode
{
    /// <summary>記号なし: タスク・コマンド・移動のすべて。</summary>
    All,
    /// <summary><c>&gt;</c> コマンドだけ（テンプレートからの作成を含む）。</summary>
    Commands,
    /// <summary><c>#</c> タグ。</summary>
    Tags,
    /// <summary><c>@</c> プロジェクト。</summary>
    Projects,
    /// <summary><c>/</c> ビュー（組み込みのビューと保存済みフィルタ）。</summary>
    Views,
    /// <summary><c>+</c> テンプレートから作る（F-15E）。</summary>
    Templates,
    /// <summary><c>?</c> ヘルプ（ショートカット一覧と記号の案内）。</summary>
    Help,
}

/// <summary>入力を絞り込み記号と検索語に分けたもの。</summary>
public sealed record PaletteQuery(PaletteMode Mode, string Term)
{
    public bool HasTerm => Term.Length > 0;

    public IReadOnlyList<string> Terms => PaletteText.Terms(Term);

    /// <summary>
    /// 先頭の1文字が記号ならその絞り込み（全角の ＞＃＠／＋？ も同じ）。日本語入力のまま「/」を打つと「・」になるので、それもビューとみなす。
    /// 記号の後ろの空白は捨てる。
    /// </summary>
    public static PaletteQuery Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return new PaletteQuery(PaletteMode.All, "");
        }
        var mode = PaletteText.Fold(trimmed[0]) switch
        {
            '>' => PaletteMode.Commands,
            '#' => PaletteMode.Tags,
            '@' => PaletteMode.Projects,
            '/' or '・' => PaletteMode.Views,
            '+' => PaletteMode.Templates,
            '?' => PaletteMode.Help,
            _ => PaletteMode.All,
        };
        return mode == PaletteMode.All
            ? new PaletteQuery(mode, trimmed)
            : new PaletteQuery(mode, trimmed[1..].Trim());
    }
}

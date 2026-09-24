using System.Globalization;
using System.Text;

namespace TaskDeck.Core.Services;

/// <summary>
/// 文字の正規化。検索キー・タグ名・パーサの前処理で同じ規則を使う。
/// NFKC で全角英数・全角記号・全角空白を半角に寄せ、検索用はさらに小文字化する。
/// </summary>
public static class TextNormalizer
{
    /// <summary>検索用: NFKC → 小文字 → 空白の連続を1つに。null は空文字。</summary>
    public static string ForSearch(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var nfkc = ReplaceLoneSurrogates(text).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return CollapseWhitespace(nfkc);
    }

    /// <summary>DB に保存する検索キー（タイトルとメモをまとめたもの）。</summary>
    public static string SearchKey(string title, string? notes) =>
        notes is null ? ForSearch(title) : ForSearch(title) + "\n" + ForSearch(notes);

    /// <summary>名前（タグ・プロジェクト）: NFKC → 前後空白除去 → 空白の連続を1つに。大文字小文字はそのまま。</summary>
    public static string ForName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        return CollapseWhitespace(ReplaceLoneSurrogates(text).Normalize(NormalizationForm.FormKC)).Trim();
    }

    /// <summary>タイトル: 改行とタブを空白にして前後を除き、書記素で maxLength までに切る。</summary>
    public static string ForTitle(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        var sb = new StringBuilder(text.Length);
        foreach (var ch in ReplaceLoneSurrogates(text).Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            sb.Append(ch is '\r' or '\n' or '\t' ? ' ' : ch);
        }
        var s = sb.ToString().Trim();
        return TruncateGraphemes(s, maxLength);
    }

    /// <summary>書記素（絵文字などを1文字と数える）の数。</summary>
    public static int GraphemeCount(string text) => new StringInfo(text).LengthInTextElements;

    public static string TruncateGraphemes(string text, int maxLength)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= maxLength ? text : info.SubstringByTextElements(0, maxLength);
    }

    /// <summary>LIKE の特殊文字（\ % _）をエスケープする。エスケープ文字は '\'。</summary>
    public static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>
    /// 対になっていないサロゲート（貼り付けで壊れた絵文字の片割れなど）を U+FFFD に置き換える。
    /// そのまま <see cref="string.Normalize(NormalizationForm)"/> に渡すと ArgumentException になるため。
    /// </summary>
    public static string ReplaceLoneSurrogates(string text)
    {
        char[]? buffer = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i]))
            {
                continue;
            }
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }
            buffer ??= text.ToCharArray();
            buffer[i] = '�';
        }
        return buffer is null ? text : new string(buffer);
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasSpace = false;
        foreach (var ch in s)
        {
            if (ch == '\n')
            {
                sb.Append('\n');
                lastWasSpace = false;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }
                lastWasSpace = true;
                continue;
            }
            sb.Append(ch);
            lastWasSpace = false;
        }
        return sb.ToString();
    }
}

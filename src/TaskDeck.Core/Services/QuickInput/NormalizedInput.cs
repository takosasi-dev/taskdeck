using System.Text;

namespace TaskDeck.Core.Services.QuickInput;

/// <summary>
/// クイック入力の前処理（設計書 4.2 の 1 と、未着手領域 4.1「全角スペース・全角記号」）。
///
/// - NFKC で全角英数・全角記号・全角空白を半角に寄せる（「１５：００」→「15:00」「＃仕事」→「#仕事」）
/// - エスケープ（<c>\#</c> <c>\@</c> <c>\!</c> <c>\*</c>）は <c>\</c> を外し、記号に「打ち消し済み」の印を付ける
/// - 正規化後の位置から<b>元の入力での位置</b>に戻せる（チップ表示・初回起動画面の色分けに使う）
///
/// 正規化は1文字（サロゲートペアは2要素で1文字）ずつ行う。文字数が増減しても対応表で位置を保てるようにするため。
/// </summary>
internal sealed class NormalizedInput
{
    private readonly int[] _originStart;
    private readonly int[] _originEnd;

    private NormalizedInput(string text, int[] originStart, int[] originEnd, bool[] isEscaped, IReadOnlyList<int> escapeBackslashes)
    {
        Text = text;
        _originStart = originStart;
        _originEnd = originEnd;
        IsEscaped = isEscaped;
        EscapeBackslashes = escapeBackslashes;
    }

    /// <summary>正規化後の文字列。解釈はこれに対して行う。</summary>
    public string Text { get; }

    /// <summary>Text[i] が <c>\</c> で打ち消された記号か（タイトルの文字として扱い、接頭辞として解釈しない）。</summary>
    public bool[] IsEscaped { get; }

    /// <summary>元の入力での、エスケープに使った <c>\</c> の位置（タイトルから取り除く）。</summary>
    public IReadOnlyList<int> EscapeBackslashes { get; }

    public static NormalizedInput Create(string input)
    {
        var text = new StringBuilder(input.Length);
        var starts = new List<int>(input.Length);
        var ends = new List<int>(input.Length);
        var escaped = new List<bool>(input.Length);
        var backslashes = new List<int>();

        for (var i = 0; i < input.Length; i++)
        {
            var isEscape = false;
            if (input[i] == '\\' && i + 1 < input.Length && IsPrefixSymbol(Normalize(UnitAt(input, i + 1))))
            {
                backslashes.Add(i);
                isEscape = true;
                i++;
            }
            var unit = UnitAt(input, i);
            var normalized = Normalize(unit);
            foreach (var ch in normalized)
            {
                text.Append(ch);
                starts.Add(i);
                ends.Add(i + unit.Length);
                escaped.Add(isEscape);
            }
            i += unit.Length - 1;
        }

        return new NormalizedInput(text.ToString(), [.. starts], [.. ends], [.. escaped], backslashes);
    }

    /// <summary>正規化後の範囲を、元の入力での範囲（開始位置と長さ）に直す。</summary>
    public (int Start, int Length) ToOriginal(int start, int length)
    {
        var from = _originStart[start];
        var to = _originEnd[start + length - 1];
        return (from, to - from);
    }

    private static bool IsPrefixSymbol(string s) => s.Length == 1 && s[0] is '#' or '@' or '!' or '*';

    /// <summary>index の1文字（サロゲートペアなら2要素）。</summary>
    private static string UnitAt(string s, int index) =>
        char.IsHighSurrogate(s[index]) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1])
            ? s.Substring(index, 2)
            : s.Substring(index, 1);

    private static string Normalize(string unit)
    {
        try
        {
            return unit.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // 対になっていないサロゲートなど、Unicode として壊れた1文字。置換文字にして先へ進む
            // （そのまま通すと TextNormalizer.ForName など他の正規化でも例外になる）。元の入力は触らないのでタイトルには残る
            return "�";
        }
    }
}

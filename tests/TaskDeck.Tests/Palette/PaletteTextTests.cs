using TaskDeck.App.Views.Palette;

namespace TaskDeck.Tests.Palette;

public class PaletteTextTests
{
    [Theory]
    [InlineData("ＡＢＣ１２３", "abc123")]
    [InlineData("カレンダー", "かれんだー")]
    [InlineData("設定　を開く", "設定 を開く")]
    [InlineData("Settings", "settings")]
    public void Fold_WideAndKatakana_FoldsWithoutChangingLength(string input, string expected)
    {
        var folded = PaletteText.Fold(input);

        Assert.Equal(expected, folded);
        Assert.Equal(input.Length, folded.Length);
    }

    [Fact]
    public void Match_NameStartsWithTerm_IsPrefix()
    {
        var match = PaletteText.Match("業務改善", PaletteText.Terms("業務"));

        Assert.Equal(PaletteText.Prefix, match.Strength);
        Assert.Equal([(0, 2)], match.Spans);
    }

    [Fact]
    public void Match_TermInsideName_IsContains()
    {
        var match = PaletteText.Match("テーマを切り替える", PaletteText.Terms("切り"));

        Assert.Equal(PaletteText.Contains, match.Strength);
        Assert.Equal([(4, 2)], match.Spans);
    }

    [Fact]
    public void Match_CharactersInOrder_IsLooseAndMarksEachRun()
    {
        var match = PaletteText.Match("業務改善", PaletteText.Terms("業改善"));

        Assert.Equal(PaletteText.Loose, match.Strength);
        Assert.Equal([(0, 1), (2, 2)], match.Spans);
    }

    [Fact]
    public void Match_CharactersOutOfOrder_DoesNotMatch()
    {
        Assert.False(PaletteText.Match("業務改善", PaletteText.Terms("改業")).IsMatch);
    }

    [Fact]
    public void Match_KatakanaNameAndHiraganaTerm_Matches()
    {
        Assert.Equal(PaletteText.Prefix, PaletteText.Match("カレンダー", PaletteText.Terms("かれん")).Strength);
    }

    [Fact]
    public void Match_SeveralTerms_AllMustMatchAndWeakestWins()
    {
        var both = PaletteText.Match("会議資料まとめる", PaletteText.Terms("会議 まとめ"));
        var missing = PaletteText.Match("会議資料まとめる", PaletteText.Terms("会議 予約"));

        Assert.Equal(PaletteText.Contains, both.Strength);
        Assert.False(missing.IsMatch);
    }

    [Fact]
    public void Match_OnlyKeywordHits_IsContainsWithoutHighlight()
    {
        var match = PaletteText.Match("設定を開く", PaletteText.Terms("せってい"), "せってい settings");

        Assert.Equal(PaletteText.Contains, match.Strength);
        Assert.Empty(match.Spans);
    }

    [Fact]
    public void Match_NoTerms_MatchesEverythingEqually()
    {
        Assert.Equal(PaletteText.Contains, PaletteText.Match("なんでも", []).Strength);
    }

    [Fact]
    public void MatchTitle_TermOnlyInNotes_IsWeakest()
    {
        var title = PaletteText.MatchTitle("請求書を出す", PaletteText.Terms("経理"));
        var prefix = PaletteText.MatchTitle("請求書を出す", PaletteText.Terms("請求"));
        var contains = PaletteText.MatchTitle("今月の請求書", PaletteText.Terms("請求"));

        Assert.Equal(PaletteText.Loose, title.Strength);
        Assert.Equal(PaletteText.Prefix, prefix.Strength);
        Assert.Equal(PaletteText.Contains, contains.Strength);
    }

    [Fact]
    public void Pieces_SpansWithOffset_SplitsIntoMatchedAndPlain()
    {
        var pieces = PaletteText.Pieces("プロジェクト「業務改善」を開く", [(0, 2)], offset: 7);

        Assert.Equal(
            [new TextPiece("プロジェクト「", false), new TextPiece("業務", true), new TextPiece("改善」を開く", false)],
            pieces);
    }

    [Theory]
    [InlineData("", PaletteMode.All, "")]
    [InlineData("会議 資料", PaletteMode.All, "会議 資料")]
    [InlineData(">設定", PaletteMode.Commands, "設定")]
    [InlineData("＞ テーマ", PaletteMode.Commands, "テーマ")]
    [InlineData("#仕事", PaletteMode.Tags, "仕事")]
    [InlineData("＃仕事", PaletteMode.Tags, "仕事")]
    [InlineData("@業務", PaletteMode.Projects, "業務")]
    [InlineData("/今日", PaletteMode.Views, "今日")]
    [InlineData("・今日", PaletteMode.Views, "今日")]
    [InlineData("+週次", PaletteMode.Templates, "週次")]
    [InlineData("?", PaletteMode.Help, "")]
    [InlineData("？ 記号", PaletteMode.Help, "記号")]
    public void Parse_LeadingSymbol_SelectsModeAndTerm(string text, PaletteMode mode, string term)
    {
        var query = PaletteQuery.Parse(text);

        Assert.Equal(mode, query.Mode);
        Assert.Equal(term, query.Term);
    }
}

using TaskDeck.Core;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Core;

/// <summary>設計書 4.2 の処理順・日付表と、要件 3.7 の入力構文。時計は JST 2026-09-22（火）10:00 に固定。</summary>
public class QuickInputParserTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private static ParsedTaskInput Parse(string? input) => new QuickInputParser(Clock).Parse(input);

    [Fact]
    public void Parse_受け入れ例_全部の項目を取り出す()
    {
        var result = Parse("会議資料まとめる 明日 15:00 #仕事 @業務改善 !高");

        Assert.Equal("会議資料まとめる", result.Title);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, 15, 0), result.DueAt);
        Assert.True(result.DueHasTime);
        Assert.Equal(Priority.High, result.Priority);
        Assert.Equal("業務改善", result.ProjectName);
        Assert.Equal(["仕事"], result.TagNames);
        Assert.Null(result.RRule);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_繰り返しも付けた受け入れ例()
    {
        var result = Parse("会議資料まとめる 明日 15:00 #仕事 @業務改善 !高 *毎週月曜");

        Assert.Equal("会議資料まとめる", result.Title);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", result.RRule);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, 15, 0), result.DueAt);
    }

    [Fact]
    public void Parse_トークンの位置は元の入力の添字()
    {
        var result = Parse("会議資料まとめる 明日 15:00 #仕事");

        var date = Assert.Single(result.Tokens, t => t.Kind == ParsedTokenKind.Date);
        Assert.Equal(9, date.Start);
        Assert.Equal(2, date.Length);
        Assert.Equal("明日", date.Text);
        Assert.Equal("15:00", Assert.Single(result.Tokens, t => t.Kind == ParsedTokenKind.Time).Text);
        Assert.Equal("#仕事", Assert.Single(result.Tokens, t => t.Kind == ParsedTokenKind.Tag).Text);
    }

    [Fact]
    public void Parse_全角の記号と数字_半角として解釈し位置は元のまま()
    {
        var result = Parse("１５：００　打ち合わせ　＃仕事　＠業務改善　！緊急　＊毎日");

        Assert.Equal("打ち合わせ", result.Title);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 22, 15, 0), result.DueAt);
        Assert.Equal(Priority.Urgent, result.Priority);
        Assert.Equal("業務改善", result.ProjectName);
        Assert.Equal(["仕事"], result.TagNames);
        Assert.Equal("FREQ=DAILY", result.RRule);
        var time = Assert.Single(result.Tokens, t => t.Kind == ParsedTokenKind.Time);
        Assert.Equal("１５：００", time.Text);
    }

    [Theory]
    [InlineData(@"\#タグではない", "#タグではない")]
    [InlineData(@"\@メールではない", "@メールではない")]
    [InlineData(@"\!強調", "!強調")]
    [InlineData(@"\*印", "*印")]
    public void Parse_エスケープした記号_タイトルに記号のまま残る(string input, string expected)
    {
        var result = Parse(input);

        Assert.Equal(expected, result.Title);
        Assert.Empty(result.Tokens);
        Assert.Equal(Priority.None, result.Priority);
    }

    [Fact]
    public void Parse_語中の記号_トークンにしない()
    {
        var result = Parse("C#の勉強 明日");

        Assert.Equal("C#の勉強", result.Title);
        Assert.Empty(result.TagNames);
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 9, 23)), result.DueAt);
    }

    [Theory]
    [InlineData("今日", 2026, 9, 22)]
    [InlineData("きょう", 2026, 9, 22)]
    [InlineData("明日", 2026, 9, 23)]
    [InlineData("あした", 2026, 9, 23)]
    [InlineData("あす", 2026, 9, 23)]
    [InlineData("明後日", 2026, 9, 24)]
    [InlineData("あさって", 2026, 9, 24)]
    [InlineData("月曜", 2026, 9, 28)]
    [InlineData("月曜日", 2026, 9, 28)]
    [InlineData("火曜", 2026, 9, 22)]
    [InlineData("来週火曜", 2026, 9, 29)]
    [InlineData("来週月曜", 2026, 9, 28)]
    [InlineData("3日後", 2026, 9, 25)]
    [InlineData("10日後", 2026, 10, 2)]
    [InlineData("今週末", 2026, 9, 26)]
    [InlineData("9/25", 2026, 9, 25)]
    [InlineData("9月25日", 2026, 9, 25)]
    [InlineData("9/1", 2027, 9, 1)]
    [InlineData("月末", 2026, 9, 30)]
    [InlineData("2026/12/31", 2026, 12, 31)]
    public void Parse_日付表現(string word, int year, int month, int day)
    {
        var result = Parse($"タスク {word}");

        Assert.Equal("タスク", result.Title);
        Assert.False(result.DueHasTime);
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(year, month, day)), result.DueAt);
    }

    [Fact]
    public void Parse_今日がその曜日なら今日()
    {
        // 2026-09-22 は火曜
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 9, 22)), Parse("会議 火曜").DueAt);
    }

    [Fact]
    public void Parse_うるう日_次にその日が来る年()
    {
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2028, 2, 29)), Parse("記念日 2/29").DueAt);
    }

    [Fact]
    public void Parse_空白で区切らなくても末尾の日付を拾う()
    {
        var result = Parse("ゴミを出す明日");

        Assert.Equal("ゴミを出す", result.Title);
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 9, 23)), result.DueAt);
    }

    [Theory]
    [InlineData("15:00", 15, 0)]
    [InlineData("15時", 15, 0)]
    [InlineData("15時30分", 15, 30)]
    [InlineData("15時半", 15, 30)]
    [InlineData("午後3時", 15, 0)]
    [InlineData("午前9時", 9, 0)]
    [InlineData("午後3:30", 15, 30)]
    [InlineData("午前12時", 0, 0)]
    [InlineData("午後12時", 12, 0)]
    [InlineData("0:05", 0, 5)]
    public void Parse_時刻表現(string word, int hour, int minute)
    {
        var result = Parse($"打ち合わせ 明日 {word}");

        Assert.Equal("打ち合わせ", result.Title);
        Assert.True(result.DueHasTime);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, hour, minute), result.DueAt);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("15:99")]
    [InlineData("午後15時")]
    [InlineData("3時間後")]
    public void Parse_時刻として読めない形_期限にしない(string word)
    {
        var result = Parse($"作業 {word}");

        Assert.Null(result.DueAt);
        Assert.Contains(word, result.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_時刻だけ_過ぎていたら翌日()
    {
        // 現在は 10:00
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 22, 15, 0), Parse("会議 15:00").DueAt);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, 8, 0), Parse("朝会 8:00").DueAt);
    }

    [Theory]
    [InlineData("!緊急", Priority.Urgent)]
    [InlineData("!高", Priority.High)]
    [InlineData("!中", Priority.Medium)]
    [InlineData("!低", Priority.Low)]
    [InlineData("!1", Priority.Low)]
    [InlineData("!2", Priority.Medium)]
    [InlineData("!3", Priority.High)]
    [InlineData("!4", Priority.Urgent)]
    [InlineData("!", Priority.Low)]
    [InlineData("!!", Priority.Medium)]
    [InlineData("!!!", Priority.High)]
    [InlineData("!!!!", Priority.Urgent)]
    public void Parse_優先度(string word, Priority expected)
    {
        var result = Parse($"対応する {word}");

        Assert.Equal(expected, result.Priority);
        Assert.Equal("対応する", result.Title);
    }

    [Fact]
    public void Parse_優先度が複数_最後を採る()
    {
        var result = Parse("対応する !1 !4");

        Assert.Equal(Priority.Urgent, result.Priority);
        Assert.Equal("対応する", result.Title);
        Assert.Equal(2, result.Tokens.Count(t => t.Kind == ParsedTokenKind.Priority));
    }

    [Fact]
    public void Parse_読めない優先度_警告を出してタイトルに残す()
    {
        var result = Parse("対応する !最高");

        Assert.Equal(Priority.None, result.Priority);
        Assert.Equal("対応する !最高", result.Title);
        Assert.Equal("!最高", Assert.Single(result.Warnings).Token);
    }

    [Fact]
    public void Parse_タグは全部_プロジェクトは最後()
    {
        var result = Parse("買い物 #食品 #日用品 @家事 @週末");

        Assert.Equal(["食品", "日用品"], result.TagNames);
        Assert.Equal("週末", result.ProjectName);
        Assert.Equal("買い物", result.Title);
    }

    [Fact]
    public void Parse_同じタグが二度_ひとつにまとめる()
    {
        Assert.Equal(["仕事"], Parse("タスク #仕事 #仕事").TagNames);
    }

    [Theory]
    [InlineData("*毎日", "FREQ=DAILY")]
    [InlineData("*平日", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    [InlineData("*毎週", "FREQ=WEEKLY")]
    [InlineData("*毎週月曜", "FREQ=WEEKLY;BYDAY=MO")]
    [InlineData("*毎週月", "FREQ=WEEKLY;BYDAY=MO")]
    [InlineData("*毎週月曜日", "FREQ=WEEKLY;BYDAY=MO")]
    [InlineData("*毎週月水金", "FREQ=WEEKLY;BYDAY=MO,WE,FR")]
    [InlineData("*毎週火曜と木曜", "FREQ=WEEKLY;BYDAY=TU,TH")]
    [InlineData("*隔週火曜", "FREQ=WEEKLY;INTERVAL=2;BYDAY=TU")]
    [InlineData("*2週ごと", "FREQ=WEEKLY;INTERVAL=2")]
    [InlineData("*毎月1日", "FREQ=MONTHLY;BYMONTHDAY=1")]
    [InlineData("*毎月末", "FREQ=MONTHLY;BYMONTHDAY=-1")]
    [InlineData("*月末", "FREQ=MONTHLY;BYMONTHDAY=-1")]
    [InlineData("*毎月第3月曜", "FREQ=MONTHLY;BYDAY=3MO")]
    [InlineData("*毎月最終金曜", "FREQ=MONTHLY;BYDAY=-1FR")]
    [InlineData("*毎年", "FREQ=YEARLY")]
    [InlineData("*3日ごと", "FREQ=DAILY;INTERVAL=3")]
    [InlineData("*毎月", "FREQ=MONTHLY")]
    public void Parse_繰り返し(string word, string expected)
    {
        var result = Parse($"掃除 {word}");

        Assert.Equal(expected, result.RRule);
        Assert.Equal("掃除", result.Title);
    }

    [Fact]
    public void Parse_読めない繰り返し_警告を出してタイトルに残す()
    {
        var result = Parse("掃除 *毎週土日祝");

        Assert.Null(result.RRule);
        Assert.Equal("掃除 *毎週土日祝", result.Title);
        Assert.Equal("*毎週土日祝", Assert.Single(result.Warnings).Token);
    }

    [Fact]
    public void Parse_繰り返しだけで日付なし_今日以降の最初の発生日()
    {
        // 2026-09-22（火）に「毎週月曜」→ 9/28
        var result = Parse("定例 *毎週月曜");

        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 9, 28)), result.DueAt);
        Assert.False(result.DueHasTime);
    }

    [Fact]
    public void Parse_繰り返しだけ_今日が発生日なら今日()
    {
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 9, 22)), Parse("日報 *平日").DueAt);
    }

    [Fact]
    public void Parse_日付があれば繰り返しより日付を優先()
    {
        var result = Parse("定例 10/1 *毎週月曜");

        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 10, 1)), result.DueAt);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", result.RRule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　")]
    public void Parse_空の入力_空のタイトル(string? input)
    {
        var result = Parse(input);

        Assert.Equal("", result.Title);
        Assert.Null(result.DueAt);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public void Parse_全部がトークン_入力全体をタイトルにする()
    {
        var result = Parse("*毎日");

        Assert.Equal("*毎日", result.Title);
        Assert.Equal("FREQ=DAILY", result.RRule);
    }

    [Fact]
    public void Parse_500文字を超えるタイトル_書記素で切る()
    {
        var result = Parse(string.Concat(Enumerable.Repeat("👍", 600)) + " 明日");

        Assert.Equal(500, TextNormalizer.GraphemeCount(result.Title));
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 9, 23)), result.DueAt);
    }

    [Fact]
    public void Parse_改行とタブ_空白にして1行にする()
    {
        Assert.Equal("会議 資料", Parse("会議\r\n\t資料").Title);
    }

    [Fact]
    public void Parse_でたらめな文字列を大量に入れても例外にならない()
    {
        var random = new Random(20260923);
        var alphabet = "あ漢字#@!*\\/:時分日曜週月末今明後々0123456789 　\t\n👍ｱﾞｰ()[]%_".ToCharArray();
        for (var i = 0; i < 400; i++)
        {
            var length = random.Next(0, 80);
            var input = new string([.. Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)])]);

            var result = Parse(input);

            Assert.NotNull(result.Title);
            Assert.True(TextNormalizer.GraphemeCount(result.Title) <= 500);
        }
    }

    [Fact]
    public void Parse_サロゲートペアや壊れた文字を混ぜても例外にならない()
    {
        var result = Parse("🎉\ud800 お祝い 明日");

        Assert.Contains("お祝い", result.Title, StringComparison.Ordinal);
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 9, 23)), result.DueAt);
    }

    [Fact]
    public void ToRequest_繰り返しをRecurrenceInputに移す()
    {
        var request = Parse("掃除 *毎日 #家事").ToRequest();

        Assert.Equal("掃除", request.Title);
        Assert.Equal("FREQ=DAILY", request.Recurrence?.RRule);
        Assert.Equal(["家事"], request.TagNames);
    }
}

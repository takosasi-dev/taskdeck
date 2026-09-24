using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>一覧の期限欄と読み上げの文言（UI 設計書 6.1・8章）。時計は JST 2026-09-22（火）10:00。</summary>
public class DisplayTextTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private static TaskItem Due(int month, int day, int? hour = null, int minute = 0, TaskItemStatus status = TaskItemStatus.NotStarted) => new()
    {
        Title = "会議資料まとめる",
        Status = status,
        DueAt = hour is { } h ? FixedClock.LocalToUtc(2026, month, day, h, minute) : FixedClock.LocalToUtc(2026, month, day),
        DueHasTime = hour is not null,
    };

    [Fact]
    public void DueColumn_今日の時刻あり_時刻を濃く出す()
    {
        var due = DisplayText.DueColumn(Due(9, 22, 15), Clock);

        Assert.Equal("15:00", due.Text);
        Assert.True(due.IsStrong);
        Assert.False(due.IsOverdue);
    }

    [Fact]
    public void DueColumn_今日の日付のみ_終日()
    {
        var due = DisplayText.DueColumn(Due(9, 22), Clock);

        Assert.Equal("終日", due.Text);
        Assert.False(due.IsStrong);
    }

    [Fact]
    public void DueColumn_明日_明日()
    {
        Assert.Equal("明日", DisplayText.DueColumn(Due(9, 23, 9), Clock).Text);
    }

    [Fact]
    public void DueColumn_それ以外_月と日()
    {
        Assert.Equal("9/25", DisplayText.DueColumn(Due(9, 25), Clock).Text);
    }

    [Fact]
    public void DueColumn_3日前の期限_3日超過を赤で出す()
    {
        var due = DisplayText.DueColumn(Due(9, 19), Clock);

        Assert.Equal("3日超過", due.Text);
        Assert.True(due.IsOverdue);
    }

    [Fact]
    public void DueColumn_今日の過ぎた時刻_時刻を赤で出す()
    {
        var due = DisplayText.DueColumn(Due(9, 22, 9, 30), Clock);

        Assert.Equal("9:30", due.Text);
        Assert.True(due.IsOverdue);
    }

    [Fact]
    public void DueColumn_完了した過去の期限_超過にしない()
    {
        var due = DisplayText.DueColumn(Due(9, 19, status: TaskItemStatus.Completed), Clock);

        Assert.Equal("9/19", due.Text);
        Assert.False(due.IsOverdue);
    }

    [Fact]
    public void DueColumn_期限なし_空()
    {
        var due = DisplayText.DueColumn(new TaskItem { Title = "x" }, Clock);

        Assert.False(due.HasText);
    }

    [Fact]
    public void RowAutomationName_未完了で優先度と時刻あり_状態優先度タイトル期限の順に読む()
    {
        var task = Due(9, 22, 15);
        task.Priority = Priority.High;

        Assert.Equal("未完了、優先度高、会議資料まとめる、今日15時", DisplayText.RowAutomationName(task, Clock));
    }

    [Fact]
    public void RowAutomationName_期限切れ_超過日数を読む()
    {
        Assert.Equal("未完了、会議資料まとめる、2日超過", DisplayText.RowAutomationName(Due(9, 20), Clock));
    }

    [Fact]
    public void DayHeader_今日と明日と昨日_前に付ける()
    {
        var today = new DateOnly(2026, 9, 22);

        Assert.Equal("今日・9月22日（火）", DisplayText.DayHeader(today, today));
        Assert.Equal("明日・9月23日（水）", DisplayText.DayHeader(today.AddDays(1), today));
        Assert.Equal("昨日・9月21日（月）", DisplayText.DayHeader(today.AddDays(-1), today));
        Assert.Equal("9月25日（金）", DisplayText.DayHeader(today.AddDays(3), today));
    }

    [Fact]
    public void Quote_長いタイトル_20文字で切って文の後ろを残す()
    {
        Assert.Equal("「会議資料まとめる」", DisplayText.Quote("会議資料まとめる"));
        Assert.Equal("「" + new string('あ', 20) + "…」", DisplayText.Quote(new string('あ', 30)));
    }

    [Fact]
    public void DueShort_明日の時刻あり_明日と時刻()
    {
        Assert.Equal("明日 15:00", DisplayText.DueShort(FixedClock.LocalToUtc(2026, 9, 23, 15), true, Clock));
        Assert.Equal("9/30", DisplayText.DueShort(FixedClock.LocalToUtc(2026, 9, 30), false, Clock));
    }

    [Theory]
    [InlineData(null, "なし")]
    [InlineData(45, "45分")]
    [InlineData(60, "1時間")]
    [InlineData(75, "1時間15分")]
    [InlineData(105, "1時間45分")]
    [InlineData(120, "2時間")]
    [InlineData(135, "2時間15分")]
    public void DurationName_選択肢に無い値も_時間と分で出す(int? minutes, string expected)
    {
        Assert.Equal(expected, DisplayText.DurationName(minutes));
    }
}

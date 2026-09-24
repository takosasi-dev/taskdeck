using TaskDeck.App.Views.Focus;
using TaskDeck.Core.Entities;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Palette;

public class FocusTextTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private static TaskItem Due(int day, int? hour = null, int minute = 0) => new()
    {
        Title = "t",
        DueAt = hour is { } h ? FixedClock.LocalToUtc(2026, 9, day, h, minute) : FixedClock.LocalToUtc(2026, 9, day),
        DueHasTime = hour is not null,
    };

    [Theory]
    [InlineData(22, 11, 12, "あと 1 時間 12 分")]
    [InlineData(22, 10, 45, "あと 45 分")]
    [InlineData(22, 12, 0, "あと 2 時間")]
    [InlineData(22, 9, 30, "30 分超過")]
    [InlineData(20, 9, 0, "2 日超過")]
    public void Remaining_TimedDue_ShowsTimeLeftOrOver(int day, int hour, int minute, string expected)
    {
        Assert.Equal(expected, FocusText.Remaining(Due(day, hour, minute), Clock));
    }

    [Theory]
    [InlineData(22, "今日中")]
    [InlineData(20, "2 日超過")]
    public void Remaining_DateOnlyDue_ShowsTodayOrDaysOver(int day, string expected)
    {
        Assert.Equal(expected, FocusText.Remaining(Due(day), Clock));
    }

    [Theory]
    [InlineData(22, 15, "15:00 まで")]
    [InlineData(20, 9, "9/20 9:00 まで")]
    [InlineData(22, null, "今日まで")]
    [InlineData(20, null, "9/20 まで")]
    public void DueLabel_TodayOrOtherDay_ShowsDateOnlyWhenNotToday(int day, int? hour, string expected)
    {
        Assert.Equal(expected, FocusText.DueLabel(Due(day, hour), Clock));
    }

    [Fact]
    public void Deadline_DateOnly_IsEndOfThatDay()
    {
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23), FocusText.Deadline(Due(22), Clock));
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 22, 15, 0), FocusText.Deadline(Due(22, 15), Clock));
    }

    [Fact]
    public void NotesHead_Long_CutsAtLimit()
    {
        var head = FocusText.NotesHead(new string('あ', FocusText.NotesHeadLength + 10));

        Assert.Equal(FocusText.NotesHeadLength + 1, head?.Length);
        Assert.EndsWith("…", head, StringComparison.Ordinal);
        Assert.Null(FocusText.NotesHead("   "));
    }
}

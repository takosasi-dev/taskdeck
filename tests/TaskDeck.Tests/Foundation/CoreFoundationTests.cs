using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Foundation;

public class ClockExtensionsTests
{
    [Fact]
    public void LocalDayStartUtc_JstDate_IsPreviousDay15Utc()
    {
        var clock = FixedClock.AtLocal(2026, 9, 23, 0, 30);

        Assert.Equal(new DateTime(2026, 9, 22, 15, 0, 0, DateTimeKind.Utc), clock.LocalDayStartUtc(new DateOnly(2026, 9, 23)));
        Assert.Equal(DateTimeKind.Utc, clock.LocalDayStartUtc(new DateOnly(2026, 9, 23)).Kind);
    }

    [Fact]
    public void LocalToday_JustAfterLocalMidnight_IsNewDate()
    {
        // UTC ではまだ 9/22 だが、ローカルでは 9/23
        var clock = FixedClock.AtLocal(2026, 9, 23, 0, 30);

        Assert.Equal(new DateOnly(2026, 9, 23), clock.LocalToday());
    }

    [Fact]
    public void ToLocalDate_LateNightUtc_BelongsToNextLocalDay()
    {
        var clock = FixedClock.AtLocal(2026, 9, 23);

        Assert.Equal(new DateOnly(2026, 9, 23), clock.ToLocalDate(new DateTime(2026, 9, 22, 16, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ToUtc_RoundTripsThroughToLocal()
    {
        var clock = FixedClock.AtLocal(2026, 1, 1);
        var utc = new DateTime(2026, 2, 28, 23, 59, 0, DateTimeKind.Utc);

        Assert.Equal(utc, clock.ToUtc(clock.ToLocal(utc)));
    }
}

public class TextNormalizerTests
{
    [Theory]
    [InlineData("ＡＷＳ課題", "aws課題")]
    [InlineData("会議　資料", "会議 資料")]
    [InlineData("  Hello   World ", " hello world ")]
    [InlineData(null, "")]
    public void ForSearch_NormalizesWidthCaseAndSpaces(string? input, string expected) =>
        Assert.Equal(expected, TextNormalizer.ForSearch(input));

    [Fact]
    public void ForTitle_ReplacesNewlinesAndTrims() =>
        Assert.Equal("a b", TextNormalizer.ForTitle("  a\r\nb \t", 500));

    [Fact]
    public void ForTitle_CountsEmojiAsOneCharacter()
    {
        var title = TextNormalizer.ForTitle(string.Concat(Enumerable.Repeat("👍", 600)), 500);

        Assert.Equal(500, TextNormalizer.GraphemeCount(title));
    }

    [Fact]
    public void ForSearch_LoneSurrogate_DoesNotThrow()
    {
        // 壊れた絵文字の片割れ（貼り付けで起きる）。string.Normalize はそのままだと ArgumentException
        var broken = "会議\uD83D資料";

        Assert.Equal("会議�資料", TextNormalizer.ForSearch(broken));
        Assert.Equal("会議�資料", TextNormalizer.ForName(broken));
        Assert.Equal("会議�資料", TextNormalizer.ForTitle(broken, 500));
    }

    [Fact]
    public void ForSearch_ValidSurrogatePair_IsKept() =>
        Assert.Equal("👍a", TextNormalizer.ForSearch("👍A"));

    [Fact]
    public void EscapeLike_EscapesWildcards() =>
        Assert.Equal("100\\%\\_\\\\", TextNormalizer.EscapeLike("100%_\\"));
}

public class UndoStackTests
{
    private static UndoEntry Entry(string label, UndoKind kind) =>
        new(label, new ChangeSet([], [Guid.NewGuid()], []), kind);

    [Fact]
    public void Push_MoreThanCapacity_DropsOldest()
    {
        var stack = new UndoStack();
        for (var i = 0; i < 12; i++)
        {
            stack.Push(Entry($"#{i}", UndoKind.Toggle));
        }

        Assert.Equal(UndoStack.Capacity, stack.Count);
        Assert.Equal("#11", stack.Pop()!.Label);
    }

    [Fact]
    public void Push_TextEdit_KeepsOnlyLatestTextEdit()
    {
        var stack = new UndoStack();
        stack.Push(Entry("edit1", UndoKind.TextEdit));
        stack.Push(Entry("toggle", UndoKind.Toggle));
        stack.Push(Entry("edit2", UndoKind.TextEdit));

        Assert.Equal(2, stack.Count);
        Assert.Equal("edit2", stack.Pop()!.Label);
        Assert.Equal("toggle", stack.Pop()!.Label);
        Assert.Null(stack.Pop());
    }

    [Fact]
    public void Push_EmptyChangeSet_IsIgnored()
    {
        var stack = new UndoStack();
        stack.Push(new UndoEntry("noop", ChangeSet.Empty, UndoKind.Other));

        Assert.Equal(0, stack.Count);
    }
}

public class TaskRulesTests
{
    [Fact]
    public void IsOverdue_DateOnlyDueToday_IsNotOverdueEvenLateAtNight()
    {
        var clock = FixedClock.AtLocal(2026, 9, 22, 23, 50);
        var task = new TaskItem { DueAt = clock.LocalDayStartUtc(new DateOnly(2026, 9, 22)), DueHasTime = false };

        Assert.False(TaskRules.IsOverdue(task, clock));
    }

    [Fact]
    public void IsOverdue_DateOnlyDueYesterday_IsOverdue()
    {
        var clock = FixedClock.AtLocal(2026, 9, 22, 0, 10);
        var task = new TaskItem { DueAt = clock.LocalDayStartUtc(new DateOnly(2026, 9, 21)), DueHasTime = false };

        Assert.True(TaskRules.IsOverdue(task, clock));
    }

    [Fact]
    public void IsOverdue_TimedDueEarlierToday_IsOverdue()
    {
        var clock = FixedClock.AtLocal(2026, 9, 22, 16, 0);
        var task = new TaskItem { DueAt = FixedClock.LocalToUtc(2026, 9, 22, 15, 0), DueHasTime = true };

        Assert.True(TaskRules.IsOverdue(task, clock));
    }

    [Fact]
    public void IsOverdue_CompletedTask_IsNeverOverdue()
    {
        var clock = FixedClock.AtLocal(2026, 9, 22, 16, 0);
        var task = new TaskItem { DueAt = FixedClock.LocalToUtc(2026, 9, 1), Status = TaskItemStatus.Completed };

        Assert.False(TaskRules.IsOverdue(task, clock));
    }

    [Fact]
    public void ComputeRemindAt_DateOnly_UsesDefaultReminderTimeMinusOffset()
    {
        var clock = FixedClock.AtLocal(2026, 9, 20);
        var due = clock.LocalDayStartUtc(new DateOnly(2026, 9, 22));

        var remind = TaskRules.ComputeRemindAt(due, false, 60, new TimeOnly(9, 0), clock);

        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 22, 8, 0), remind);
    }

    [Fact]
    public void ComputeRemindAt_Timed_IsDueMinusOffset()
    {
        var clock = FixedClock.AtLocal(2026, 9, 20);
        var due = FixedClock.LocalToUtc(2026, 9, 22, 15, 0);

        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 22, 14, 45), TaskRules.ComputeRemindAt(due, true, 15, new TimeOnly(9, 0), clock));
    }
}

public class SortOrderMathTests
{
    [Fact]
    public void Between_Midpoint() => Assert.Equal(1.5, SortOrderMath.Between(1, 2));

    [Fact]
    public void Between_OpenEnds() => Assert.Equal(1024 + 3, SortOrderMath.Between(3, null));
}

public class TaskTreeTests
{
    private static TaskListRow Row(Guid id, Guid? parent, double order, bool context = false) =>
        new(new TaskItem { Id = id, ParentTaskId = parent, SortOrder = order, Title = id.ToString() }, [], 0, 0, context);

    [Fact]
    public void Arrange_PutsChildrenRightAfterParentInSortOrder()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), a1 = Guid.NewGuid(), a2 = Guid.NewGuid(), a1x = Guid.NewGuid();
        var rows = new[] { Row(b, null, 1), Row(a2, a, 2, true), Row(a, null, 2), Row(a1x, a1, 1, true), Row(a1, a, 1, true) };

        var arranged = TaskTree.Arrange(rows);

        Assert.Equal([b, a, a1, a1x, a2], arranged.Select(r => r.Row.Task.Id));
        Assert.Equal([0, 0, 1, 2, 1], arranged.Select(r => r.Level));
    }

    [Fact]
    public void Arrange_ChildWhoseParentIsNotInResult_IsRoot()
    {
        Guid orphan = Guid.NewGuid();
        var arranged = TaskTree.Arrange([Row(orphan, Guid.NewGuid(), 1)]);

        Assert.Equal(0, Assert.Single(arranged).Level);
    }
}

public class ViewKeyTests
{
    [Fact]
    public void ToStringAndTryParse_RoundTrip()
    {
        var key = ViewKey.ForProject(Guid.NewGuid());

        Assert.True(ViewKey.TryParse(key.ToString(), out var parsed));
        Assert.Equal(key, parsed);
    }

    [Fact]
    public void TryParse_Garbage_ReturnsFalseAndToday()
    {
        Assert.False(ViewKey.TryParse("Project:not-a-guid", out var parsed));
        Assert.Equal(ViewKey.Today, parsed);
    }
}

public class RecurrencePresetsTests
{
    [Fact]
    public void Weekly_OrdersDaysFromMonday() =>
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,FR,SU", RecurrencePresets.Weekly([DayOfWeek.Sunday, DayOfWeek.Friday, DayOfWeek.Monday]));

    [Theory]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", true)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", true)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,SA", false)]
    [InlineData("FREQ=DAILY", false)]
    public void IsWeekdaysOnly(string rrule, bool expected) =>
        Assert.Equal(expected, RecurrencePresets.IsWeekdaysOnly(rrule));
}

public class BusinessDaysTests
{
    [Fact]
    public void Next_SkipsWeekendAndHoliday()
    {
        // 2026-09-18(金) の次 → 9/19土・9/20日・9/21(月・敬老の日) を飛ばして 9/22(火)
        var holidays = new FixedHolidays(new DateOnly(2026, 9, 21));

        Assert.Equal(new DateOnly(2026, 9, 22), BusinessDays.Next(new DateOnly(2026, 9, 18), holidays));
    }
}

public class ProjectPaletteTests
{
    [Fact]
    public void NextColor_PicksFirstUnusedInOrder() =>
        Assert.Equal("#CA5010", ProjectPalette.NextColor(["#0067C0", "#8764B8"]));

    [Fact]
    public void NextColor_AllUsed_PicksLeastUsed() =>
        Assert.Equal("#CA5010", ProjectPalette.NextColor([.. ProjectPalette.Light, "#0067C0"]));
}

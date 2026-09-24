using NSubstitute;
using TaskDeck.App.Views.Calendar;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Calendar;

/// <summary>カレンダーの ViewModel（読み込み・移動・選択・ドラッグの書き込み）。時計は JST 2026-09-22（火）10:00。</summary>
public class CalendarViewModelTests
{
    private readonly CalendarFixture _f = new();

    private static DateOnly Day(int month, int day) => CalendarFixture.Day(month, day);

    // ---- 読み込みと移動 ----

    [Fact]
    public async Task ActivateAsync_月表示_6週ぶんを完了も含めて読む()
    {
        var viewModel = _f.Create();

        await viewModel.ActivateAsync();

        var query = Assert.Single(_f.RangeQueries);
        Assert.Equal(FixedClock.LocalToUtc(2026, 8, 30), query.From);
        Assert.Equal(FixedClock.LocalToUtc(2026, 10, 11), query.To);
        Assert.True(query.IncludeClosed);
        Assert.Equal("2026年9月", viewModel.Title);
        Assert.Equal(42, viewModel.MonthDays.Count);
        Assert.Equal(Day(8, 30), viewModel.MonthDays[0].Date);
        Assert.Empty(viewModel.WeekDays);
        Assert.True(CalendarFixture.DayOf(viewModel, Day(9, 22)).IsToday);
    }

    [Fact]
    public async Task ActivateAsync_月曜始まりの設定_月曜から並べる()
    {
        _f.Settings.Current.Calendar.WeekStartsOnMonday = true;
        var viewModel = _f.Create();

        await viewModel.ActivateAsync();

        Assert.Equal(Day(8, 31), viewModel.MonthDays[0].Date);
        Assert.Equal(DayOfWeek.Monday, viewModel.Weekdays[0].Day);
        Assert.Equal("月", viewModel.Weekdays[0].Name);
        Assert.Equal(DayOfWeek.Sunday, viewModel.Weekdays[6].Day);
    }

    [Fact]
    public async Task ActivateAsync_変わっていなければ_読み直さない()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        viewModel.Deactivate();

        await viewModel.ActivateAsync();

        Assert.Single(_f.RangeQueries);
    }

    [Fact]
    public async Task NextCommand_月表示_次の月を読む()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();

        await viewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal("2026年10月", viewModel.Title);
        // 10/1 は木曜 → 日曜始まりなら 9/27 から
        Assert.Equal(Day(9, 27), viewModel.MonthDays[0].Date);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 27), _f.RangeQueries[^1].From);
        Assert.False(CalendarFixture.DayOf(viewModel, Day(10, 15)).IsOtherMonth);
        Assert.True(CalendarFixture.DayOf(viewModel, Day(9, 30)).IsOtherMonth);
    }

    [Fact]
    public async Task TodayCommand_前の月へ移った後_今月に戻る()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        await viewModel.PreviousCommand.ExecuteAsync(null);
        await viewModel.PreviousCommand.ExecuteAsync(null);
        Assert.Equal("2026年7月", viewModel.Title);

        await viewModel.TodayCommand.ExecuteAsync(null);

        Assert.Equal("2026年9月", viewModel.Title);
        Assert.Equal(Day(9, 22), viewModel.Anchor);
    }

    [Theory]
    [InlineData(false, "9月20日〜26日", 20)]
    [InlineData(true, "9月21日〜27日", 21)]
    public async Task ShowWeekCommand_今月から_今日の週を週の開始曜日から出す(bool mondayStart, string title, int firstDay)
    {
        _f.Settings.Current.Calendar.WeekStartsOnMonday = mondayStart;
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();

        await viewModel.ShowWeekCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsWeek);
        Assert.Equal(title, viewModel.Title);
        Assert.Equal(7, viewModel.WeekDays.Count);
        Assert.Equal(Day(9, firstDay), viewModel.WeekDays[0].Date);
        Assert.Empty(viewModel.MonthDays);
        Assert.True(viewModel.IsTodayInWeek);
        Assert.Equal(10 * 60, viewModel.NowMinute);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, firstDay), _f.RangeQueries[^1].From);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, firstDay + 7), _f.RangeQueries[^1].To);
        Assert.Equal("前の週", viewModel.PreviousLabel);
    }

    [Fact]
    public async Task NextCommand_週表示_次の週で今日の線は出さない()
    {
        _f.Settings.Current.Calendar.WeekStartsOnMonday = true;
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        await viewModel.ShowWeekCommand.ExecuteAsync(null);

        await viewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal("9月28日〜10月4日", viewModel.Title);
        Assert.False(viewModel.IsTodayInWeek);
    }

    [Fact]
    public async Task ShowWeekCommand_別の月から_その月の最初の週()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        await viewModel.NextCommand.ExecuteAsync(null);

        await viewModel.ShowWeekCommand.ExecuteAsync(null);

        Assert.Equal("9月27日〜10月3日", viewModel.Title);
    }

    [Fact]
    public async Task ShowMonthCommand_週表示から_週の真ん中の日の月に戻る()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        await viewModel.ShowWeekCommand.ExecuteAsync(null);
        await viewModel.NextCommand.ExecuteAsync(null); // 9/27〜10/3

        await viewModel.ShowMonthCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsMonth);
        Assert.Equal("2026年9月", viewModel.Title);
        Assert.Equal(42, viewModel.MonthDays.Count);
        Assert.Empty(viewModel.WeekDays);
    }

    [Fact]
    public async Task DataChanged_表示中_まとめて読み直す()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        var added = _f.AddTask("追加", new DateTime(2026, 9, 24));

        _f.Hub.Publish(DataChangeKind.Tasks, [added.Id]);
        _f.Hub.Publish(DataChangeKind.Tasks, [added.Id]);

        Assert.True(await CalendarFixture.WaitUntilAsync(() =>
            CalendarFixture.DayOf(viewModel, Day(9, 24)).Entries.Any(e => e.TaskId == added.Id)));
        await Task.Delay(CalendarViewModel.ChangeDebounceMs * 3);
        Assert.Equal(2, _f.RangeQueries.Count);
    }

    [Fact]
    public async Task DataChanged_画面に出ていない間_読まずに出たときに読む()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        viewModel.Deactivate();

        _f.Hub.Publish(DataChangeKind.Tasks);
        await Task.Delay(CalendarViewModel.ChangeDebounceMs * 3);
        Assert.Single(_f.RangeQueries);

        await viewModel.ActivateAsync();
        Assert.Equal(2, _f.RangeQueries.Count);
    }

    [Fact]
    public async Task DataChanged_タグだけ_読み直さない()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();

        _f.Hub.Publish(DataChangeKind.Tags);
        await Task.Delay(CalendarViewModel.ChangeDebounceMs * 3);

        Assert.Single(_f.RangeQueries);
    }

    [Fact]
    public async Task SettingsChanged_週の開始曜日_並べ直す()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();

        _f.Settings.Update(s => s.Calendar.WeekStartsOnMonday = true);

        Assert.True(await CalendarFixture.WaitUntilAsync(() => viewModel.MonthDays.Count > 0 && viewModel.MonthDays[0].Date == Day(8, 31)));
    }

    [Fact]
    public async Task SetMonthChipCapacity_セルが低い_見せる数を減らして他N件に数える()
    {
        for (var i = 0; i < 4; i++)
        {
            _f.AddTask($"タスク{i}", new DateTime(2026, 9, 24));
        }
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        var cell = viewModel.MonthCells.Single(c => c.Day?.Date == Day(9, 24));
        Assert.Equal(3, cell.Day!.VisibleEntries.Count);

        viewModel.SetMonthChipCapacity(2);

        Assert.Equal(2, cell.Day!.VisibleEntries.Count);
        Assert.Equal("他 2 件", cell.Day.MoreText);
        Assert.Null(cell.Day.Chip3);
        Assert.Single(_f.RangeQueries);

        // 読み直しても減らした数のまま
        await viewModel.NextCommand.ExecuteAsync(null);
        await viewModel.PreviousCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.MonthCells.Single(c => c.Day?.Date == Day(9, 24)).Day!.VisibleEntries.Count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 3)]
    public async Task SetMonthChipCapacity_範囲の外_1から3に収める(int requested, int expected)
    {
        for (var i = 0; i < 5; i++)
        {
            _f.AddTask($"タスク{i}", new DateTime(2026, 9, 24));
        }
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        viewModel.SetMonthChipCapacity(2);

        viewModel.SetMonthChipCapacity(requested);

        Assert.Equal(expected, CalendarFixture.DayOf(viewModel, Day(9, 24)).VisibleEntries.Count);
    }

    // ---- 選択 ----

    [Fact]
    public async Task SelectCommand_チップ_そのタスクを選んで強調する()
    {
        var task = _f.AddTask("会議", new DateTime(2026, 9, 24, 15, 0, 0), hasTime: true);
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        var entry = CalendarFixture.DayOf(viewModel, Day(9, 24)).Entries[0];

        viewModel.SelectCommand.Execute(entry);

        Assert.Equal(task.Id, viewModel.SelectedTaskId);
        Assert.True(entry.IsSelected);
    }

    [Fact]
    public async Task SelectCommand_繰り返しの仮表示_元のタスクを選ぶ()
    {
        var task = _f.AddTask("日報", new DateTime(2026, 9, 22));
        _f.AddRecurring(task, RecurrencePresets.Daily());
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        var ghost = CalendarFixture.DayOf(viewModel, Day(9, 25)).Entries.Single();
        Assert.True(ghost.IsGhost);

        viewModel.SelectCommand.Execute(ghost);

        Assert.Equal(task.Id, viewModel.SelectedTaskId);
    }

    [Fact]
    public async Task SelectedTaskId_メイン画面から入る_読み込み後も強調が付く()
    {
        var task = _f.AddTask("会議", new DateTime(2026, 9, 24));
        var other = _f.AddTask("別", new DateTime(2026, 9, 24));
        var viewModel = _f.Create();
        viewModel.SelectedTaskId = task.Id;

        await viewModel.ActivateAsync();

        var entries = CalendarFixture.DayOf(viewModel, Day(9, 24)).Entries;
        Assert.True(entries.Single(e => e.TaskId == task.Id).IsSelected);
        Assert.False(entries.Single(e => e.TaskId == other.Id).IsSelected);
    }

    // ---- ドラッグの書き込み ----

    [Fact]
    public async Task MoveToDateAsync_時刻あり_時刻を保って期限を移し取り消しに積む()
    {
        var task = _f.AddTask("会議資料", new DateTime(2026, 9, 22, 15, 0, 0), hasTime: true, durationMinutes: 60);
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        var entry = CalendarFixture.DayOf(viewModel, Day(9, 22)).Entries[0];

        await viewModel.MoveToDateAsync(entry, Day(9, 25));

        var after = Assert.Single(_f.Updates);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 25, 15, 0), after.DueAt);
        Assert.True(after.DueHasTime);
        Assert.Equal(60, after.DurationMinutes);
        var undo = _f.Stack.Peek();
        Assert.NotNull(undo);
        Assert.Equal(UndoKind.Other, undo.Kind);
        Assert.Equal("「会議資料」の期限を9/25 15:00に移しました", undo.Label);
        Assert.Equal(task.Id, undo.Changes.TasksBefore[0].Task.Id);
    }

    [Fact]
    public async Task MoveToDateAsync_日付のみ_日付のみのまま移す()
    {
        _f.AddTask("提出", new DateTime(2026, 9, 22));
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();

        await viewModel.MoveToDateAsync(CalendarFixture.DayOf(viewModel, Day(9, 22)).Entries[0], Day(9, 23));

        var after = Assert.Single(_f.Updates);
        Assert.Equal(TaskRules.DateOnlyDue(Day(9, 23), _f.Clock), after.DueAt);
        Assert.False(after.DueHasTime);
    }

    [Fact]
    public async Task MoveToTimeAsync_終日から時間軸_時刻ありにして15分単位()
    {
        _f.AddTask("提出", new DateTime(2026, 9, 23));
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        await viewModel.ShowWeekCommand.ExecuteAsync(null);
        var entry = CalendarFixture.DayOf(viewModel, Day(9, 23)).Entries[0];

        await viewModel.MoveToTimeAsync(entry, Day(9, 24), (14 * 60) + 53);

        var after = Assert.Single(_f.Updates);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 24, 15, 0), after.DueAt);
        Assert.True(after.DueHasTime);
        Assert.Null(after.DurationMinutes);
    }

    [Fact]
    public async Task MoveToAllDayAsync_時間軸から終日帯_日付のみにする()
    {
        _f.AddTask("会議", new DateTime(2026, 9, 23, 15, 0, 0), hasTime: true);
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        await viewModel.ShowWeekCommand.ExecuteAsync(null);
        var entry = CalendarFixture.DayOf(viewModel, Day(9, 23)).Blocks[0].Entry;

        await viewModel.MoveToAllDayAsync(entry, Day(9, 23));

        var after = Assert.Single(_f.Updates);
        Assert.Equal(TaskRules.DateOnlyDue(Day(9, 23), _f.Clock), after.DueAt);
        Assert.False(after.DueHasTime);
    }

    [Fact]
    public async Task ResizeBottomAsync_下端_所要時間だけを変えて取り消しに積む()
    {
        var task = _f.AddTask("作業", new DateTime(2026, 9, 23, 13, 0, 0), hasTime: true);
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        await viewModel.ShowWeekCommand.ExecuteAsync(null);
        var entry = CalendarFixture.DayOf(viewModel, Day(9, 23)).Blocks[0].Entry;

        await viewModel.ResizeBottomAsync(entry, (15 * 60) + 5);

        var after = Assert.Single(_f.Updates);
        Assert.Equal(task.DueAt, after.DueAt);
        Assert.Equal(120, after.DurationMinutes);
        // 所要時間の文言は DisplayText.DurationName（波2-E で「2時間」「1時間15分」の形にそろえた）
        Assert.Equal("「作業」の所要時間を2時間にしました", _f.Stack.Peek()!.Label);
    }

    [Fact]
    public async Task ResizeTopAsync_上端_始まりを早めて終わりは保つ()
    {
        _f.AddTask("作業", new DateTime(2026, 9, 23, 13, 0, 0), hasTime: true, durationMinutes: 60);
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        await viewModel.ShowWeekCommand.ExecuteAsync(null);
        var entry = CalendarFixture.DayOf(viewModel, Day(9, 23)).Blocks[0].Entry;

        await viewModel.ResizeTopAsync(entry, 12 * 60);

        var after = Assert.Single(_f.Updates);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, 12, 0), after.DueAt);
        Assert.Equal(120, after.DurationMinutes);
    }

    [Fact]
    public async Task MoveToDateAsync_同じ日_書かない()
    {
        _f.AddTask("提出", new DateTime(2026, 9, 22));
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();

        await viewModel.MoveToDateAsync(CalendarFixture.DayOf(viewModel, Day(9, 22)).Entries[0], Day(9, 22));

        await _f.Tasks.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, _f.Stack.Count);
    }

    [Fact]
    public async Task MoveToDateAsync_繰り返しの仮表示_動かさない()
    {
        var task = _f.AddTask("日報", new DateTime(2026, 9, 22));
        _f.AddRecurring(task, RecurrencePresets.Daily());
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        var ghost = CalendarFixture.DayOf(viewModel, Day(9, 24)).Entries.Single();

        await viewModel.MoveToDateAsync(ghost, Day(9, 28));

        await _f.Tasks.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveToDateAsync_完了したタスク_動かさない()
    {
        _f.AddTask("済み", new DateTime(2026, 9, 22), status: TaskItemStatus.Completed);
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();

        await viewModel.MoveToDateAsync(CalendarFixture.DayOf(viewModel, Day(9, 22)).Entries[0], Day(9, 24));

        await _f.Tasks.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<Action<TaskItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResizeBottomAsync_日付のみのタスク_書かない()
    {
        _f.AddTask("提出", new DateTime(2026, 9, 22));
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();

        await viewModel.ResizeBottomAsync(CalendarFixture.DayOf(viewModel, Day(9, 22)).Entries[0], 12 * 60);

        Assert.Empty(_f.Updates);
    }

    [Fact]
    public async Task OnMinuteTick_日付が変わった_今日の強調を移す()
    {
        var viewModel = _f.Create();
        await viewModel.ActivateAsync();
        _f.Clock.Advance(TimeSpan.FromHours(15)); // 9/23 1:00

        viewModel.OnMinuteTick();

        Assert.True(await CalendarFixture.WaitUntilAsync(() => CalendarFixture.DayOf(viewModel, Day(9, 23)).IsToday));
        Assert.Equal(60, viewModel.NowMinute);
    }
}

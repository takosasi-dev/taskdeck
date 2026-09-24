using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Residency;
using TaskDeck.App.Services;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Tests.Data;

namespace TaskDeck.Tests.Residency;

/// <summary>通知のボタン「完了にする」「N分後に再通知」（F-083）。本物のリポジトリ（インメモリ SQLite）。時計は JST 2026-09-22 10:00。</summary>
public class ReminderActionsTests : DataTestBase
{
    private readonly UndoStack _stack = new();
    private readonly UndoService _undo;
    private readonly List<string> _toasts = [];
    private readonly ReminderActions _actions;

    public ReminderActionsTests()
    {
        _undo = new UndoService(Tasks, _stack, NullLogger<UndoService>.Instance);
        _undo.ToastRequested += (_, e) => _toasts.Add(e.Message);
        var shell = new ShellService(Substitute.For<IServiceProvider>(), Clock, NullLogger<ShellService>.Instance);
        _actions = new ReminderActions(Tasks, _undo, Settings, Clock, shell, NullLogger<ReminderActions>.Instance);
    }

    private async Task<Guid> NotifiedAsync(string title)
    {
        var result = await Tasks.AddAsync(new NewTaskRequest
        {
            Title = title,
            DueAt = At(9, 22, 11),
            DueHasTime = true,
            RemindAt = Clock.UtcNow.AddMinutes(-1),
        });
        var id = result.Created[0].Id;
        await Tasks.MarkNotifiedAsync([id], Clock.UtcNow);
        return id;
    }

    [Fact]
    public async Task HandleAsync_Complete_CompletesAndRecordsUndoWithoutToast()
    {
        var id = await NotifiedAsync("会議資料");

        await _actions.HandleAsync(ReminderActions.Complete, id);

        Assert.Equal(TaskItemStatus.Completed, (await GetAsync(id)).Status);
        Assert.Equal("「会議資料」を完了しました", _stack.Peek()?.Label);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task CompleteAsync_ThenUndo_ReopensTask()
    {
        var id = await NotifiedAsync("会議資料");
        await _actions.CompleteAsync(id);

        await _undo.UndoLatestAsync();

        Assert.Equal(TaskItemStatus.NotStarted, (await GetAsync(id)).Status);
    }

    [Fact]
    public async Task CompleteAsync_AlreadyCompleted_DoesNothing()
    {
        var id = await NotifiedAsync("もう済んだ");
        await Tasks.SetCompletedAsync([id], true);
        var before = _stack.Count;

        await _actions.CompleteAsync(id);

        Assert.Equal(before, _stack.Count);
    }

    [Fact]
    public async Task HandleAsync_Snooze_RemindsAgainAfterSnoozeMinutes()
    {
        var id = await NotifiedAsync("電話");

        await _actions.HandleAsync(ReminderActions.Snooze, id);

        var task = await GetAsync(id);
        Assert.Equal(Clock.UtcNow.AddMinutes(30), task.RemindAt);
        Assert.Null(task.NotifiedAt);
    }

    [Fact]
    public async Task SnoozeAsync_TenMinuteSetting_UsesSetting()
    {
        Settings.Update(s => s.Notifications.SnoozeMinutes = 10);
        var id = await NotifiedAsync("電話");

        await _actions.SnoozeAsync(id);

        Assert.Equal(Clock.UtcNow.AddMinutes(10), (await GetAsync(id)).RemindAt);
    }

    [Fact]
    public async Task HandleAsync_SnoozeWithMinutesOnButton_UsesButtonMinutes()
    {
        var id = await NotifiedAsync("電話");
        Settings.Update(s => s.Notifications.SnoozeMinutes = 5); // 通知を出した後で設定を変えた

        await _actions.HandleAsync(ReminderActions.Snooze, id, minutes: 30);

        Assert.Equal(Clock.UtcNow.AddMinutes(30), (await GetAsync(id)).RemindAt);
    }

    [Fact]
    public async Task SnoozeAsync_ThenTimePasses_IsDueAgain()
    {
        var id = await NotifiedAsync("電話");
        await _actions.SnoozeAsync(id);

        Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Contains(await Tasks.GetDueRemindersAsync(Clock.UtcNow), t => t.Id == id);
    }

    [Theory]
    [InlineData(30, "30分後に再通知")]
    [InlineData(5, "5分後に再通知")]
    [InlineData(60, "1時間後に再通知")]
    public void SnoozeLabel_Minutes_MatchesButtonText(int minutes, string expected) =>
        Assert.Equal(expected, ToastNotifier.SnoozeLabel(minutes));
}

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Views.Stats;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Data.Export;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Insights;

/// <summary>
/// 振り返りの画面の ViewModel: 見えている間だけ読み、期間の切替では読み直さず、変更通知は見えていれば読み直す。
/// </summary>
public sealed class StatsViewModelTests : IDisposable
{
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 23, 10, 0);
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly IProjectRepository _projects = Substitute.For<IProjectRepository>();
    private readonly FakeSettingsStore _settings = new();
    private readonly DataChangeHub _hub = new();
    private readonly TestDatabase _db;
    private readonly List<CompletedTaskFact> _facts = [];
    private readonly StatsViewModel _viewModel;
    private int _reads;

    public StatsViewModelTests()
    {
        _db = new TestDatabase(_clock);
        _tasks.GetCompletedFactsAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Null(call.Arg<DateTime?>());   // 全期間を読む（連続日数の自己最長とヒートマップのため）
                _reads++;
                return Task.FromResult<IReadOnlyList<CompletedTaskFact>>([.. _facts]);
            });
        _projects.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Project>>([]));
        var export = new StatsExportViewModel(new MarkdownExporter(_db, _clock), _settings, _clock, NullLogger<StatsExportViewModel>.Instance);
        _viewModel = new StatsViewModel(
            _tasks, _projects, new StatsCalculator(_clock), _settings, _hub, _clock, new InlineUiDispatcher(), export, NullLogger<StatsViewModel>.Instance);
        _facts.Add(Fact(9, 21));
        _facts.Add(Fact(9, 1));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SetActiveAsync_FirstShown_ReadsOnceAndShowsThisWeek()
    {
        Assert.False(_viewModel.IsLoaded);

        await _viewModel.SetActiveAsync(true);

        Assert.Equal(1, _reads);
        Assert.True(_viewModel.IsWeek);
        Assert.Equal("1", _viewModel.Display!.HeroCount);

        // 隠してまた出しても、変わっていなければ読み直さない
        await _viewModel.SetActiveAsync(false);
        await _viewModel.SetActiveAsync(true);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task Period_Changed_RecountsWithoutReading()
    {
        await _viewModel.SetActiveAsync(true);

        _viewModel.IsMonth = true;

        Assert.Equal(StatsPeriod.ThisMonth, _viewModel.Period);
        Assert.Equal("2", _viewModel.Display!.HeroCount);
        Assert.Equal("今月 片付けたタスク", _viewModel.Display.HeroCaption);
        Assert.Equal(1, _reads);
        Assert.Equal(ExportRange.ThisMonth, _viewModel.Export.Range);   // 書き出しの期間も合わせる
    }

    [Fact]
    public async Task DataChanged_WhileHidden_ReadsWhenShownNext()
    {
        await _viewModel.SetActiveAsync(true);
        await _viewModel.SetActiveAsync(false);

        _facts.Add(Fact(9, 22));
        _hub.Publish(DataChangeKind.Tasks);
        await Task.Delay(400);
        Assert.Equal(1, _reads);   // 見えていない間は読まない

        await _viewModel.SetActiveAsync(true);
        Assert.Equal(2, _reads);
        Assert.Equal("2", _viewModel.Display!.HeroCount);
    }

    [Fact]
    public async Task DataChanged_WhileShown_ReadsAgainShortlyAfter()
    {
        await _viewModel.SetActiveAsync(true);

        _facts.Add(Fact(9, 22));
        _hub.Publish(DataChangeKind.Tasks);

        Assert.True(await WaitAsync(() => _reads == 2));
        Assert.Equal("2", _viewModel.Display!.HeroCount);
    }

    [Fact]
    public async Task DataChanged_OtherKinds_AreIgnored()
    {
        await _viewModel.SetActiveAsync(true);

        _hub.Publish(DataChangeKind.ExternalCache | DataChangeKind.Templates);
        await Task.Delay(400);

        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task WeekStartSetting_Changed_RecountsWithMondayFirst()
    {
        await _viewModel.SetActiveAsync(true);
        Assert.Equal("日", _viewModel.Display!.Weekdays[0].Label);

        _settings.Update(s => s.Calendar.WeekStartsOnMonday = true);

        Assert.Equal("月", _viewModel.Display!.Weekdays[0].Label);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task ToggleTable_SwitchesButtonText()
    {
        await _viewModel.SetActiveAsync(true);
        Assert.Equal("表で見る", _viewModel.TableButtonText);

        _viewModel.ToggleTableCommand.Execute(null);

        Assert.True(_viewModel.IsTableMode);
        Assert.Equal("グラフで見る", _viewModel.TableButtonText);
    }

    private static CompletedTaskFact Fact(int month, int day)
    {
        var completed = FixedClock.LocalToUtc(2026, month, day, 12, 0);
        return new CompletedTaskFact(Guid.NewGuid(), completed, completed.AddHours(-3), null, false, null);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
        return condition();
    }
}

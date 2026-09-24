using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Views.Stats;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Data.Export;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Insights;

/// <summary>振り返りの「Markdown で書き出す」: 期間の日付・決めた出力先へ書く・0件なら作らない・出力先を覚える（F-108・F-109）。</summary>
public sealed class StatsExportViewModelTests : IDisposable
{
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 23, 10, 0);
    private readonly FakeSettingsStore _settings = new();
    private readonly TestDatabase _db;
    private readonly TaskRepository _tasks;
    private readonly StatsExportViewModel _viewModel;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "taskdeck-export-" + Guid.NewGuid().ToString("N"));

    public StatsExportViewModelTests()
    {
        _db = new TestDatabase(_clock);
        _tasks = new TaskRepository(_db, _clock, Substitute.For<IRecurrenceEngine>(), _settings, new DataChangeHub(), NullLogger<TaskRepository>.Instance);
        _viewModel = new StatsExportViewModel(new MarkdownExporter(_db, _clock), _settings, _clock, NullLogger<StatsExportViewModel>.Instance);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Theory]
    [InlineData(ExportRange.Today, false, "2026-09-23", "2026-09-23")]
    [InlineData(ExportRange.ThisWeek, false, "2026-09-20", "2026-09-26")]
    [InlineData(ExportRange.ThisWeek, true, "2026-09-21", "2026-09-27")]
    [InlineData(ExportRange.ThisMonth, false, "2026-09-01", "2026-09-30")]
    [InlineData(ExportRange.ThisYear, false, "2026-01-01", "2026-12-31")]
    public void Dates_Range_UsesLocalCalendar(ExportRange range, bool monday, string from, string to)
    {
        _settings.Current.Calendar.WeekStartsOnMonday = monday;
        _viewModel.Range = range;

        Assert.Equal((DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)), _viewModel.Dates());
    }

    [Fact]
    public void RangeText_Today_ShowsOneDay()
    {
        _viewModel.IsToday = true;

        Assert.Equal("9月23日（水）", _viewModel.RangeText);
        Assert.Equal("TaskDeck_2026-09-23.md", _viewModel.SuggestFileName());
    }

    [Fact]
    public async Task ExportToFolder_FolderSet_WritesFileThere()
    {
        await CompleteAsync("資料を送る");
        _viewModel.SetFolder(_folder);

        await _viewModel.ExportToFolderCommand.ExecuteAsync(null);

        var path = Path.Combine(_folder, "TaskDeck_2026-09-20_2026-09-26.md");
        Assert.Equal("## 2026-09-23（水）\n\n- [x] 資料を送る\n", await File.ReadAllTextAsync(path));
        Assert.Equal("書き出しました（1 件）: TaskDeck_2026-09-20_2026-09-26.md", _viewModel.Message);
        Assert.False(_viewModel.IsError);
        Assert.Equal(path, _viewModel.LastWrittenPath);
        Assert.False(_viewModel.CanRememberLastFolder);   // もう決めてあるフォルダ
    }

    [Fact]
    public async Task ExportAsync_NothingCompleted_DoesNotCreateFile()
    {
        var path = Path.Combine(_folder, "empty.md");

        await _viewModel.ExportAsync(path);

        Assert.False(File.Exists(path));
        Assert.Equal("この期間に完了したタスクはありません（ファイルは作っていません）", _viewModel.Message);
    }

    [Fact]
    public async Task RememberLastFolder_AfterDialogExport_SetsSettingsFolder()
    {
        await CompleteAsync("資料を送る");
        Assert.Null(_viewModel.Folder);
        Assert.Equal("書き出すときに毎回たずねます", _viewModel.FolderText);
        Assert.Null(_viewModel.TargetPathInFolder());

        await _viewModel.ExportAsync(Path.Combine(_folder, "picked.md"));   // ダイアログで選んだ場所
        Assert.True(_viewModel.CanRememberLastFolder);
        _viewModel.RememberLastFolderCommand.Execute(null);

        Assert.Equal(_folder, _settings.Current.Data.MarkdownExportFolder);
        Assert.Equal(Path.Combine(_folder, "TaskDeck_2026-09-20_2026-09-26.md"), _viewModel.TargetPathInFolder());

        _viewModel.AskEveryTimeCommand.Execute(null);
        Assert.Null(_settings.Current.Data.MarkdownExportFolder);
    }

    [Fact]
    public async Task ExportToFolder_FolderGone_ShowsError()
    {
        await CompleteAsync("資料を送る");
        _viewModel.SetFolder(Path.Combine(_folder, "消えたフォルダ"));

        await _viewModel.ExportToFolderCommand.ExecuteAsync(null);

        Assert.True(_viewModel.IsError);
        Assert.Equal("出力先のフォルダが見つかりません。フォルダを選び直してください", _viewModel.Message);
    }

    [Fact]
    public void OnPeriodShown_PanelClosed_FollowsStatsPeriod_ButNotWhileOpen()
    {
        _viewModel.OnPeriodShown(StatsPeriod.ThisYear);
        Assert.True(_viewModel.IsYear);

        _viewModel.ToggleCommand.Execute(null);   // 開く
        _viewModel.IsToday = true;
        _viewModel.OnPeriodShown(StatsPeriod.ThisMonth);

        Assert.True(_viewModel.IsToday);   // 開いている間は選んだ期間のまま
    }

    private async Task CompleteAsync(string title) =>
        await _tasks.AddAsync(new NewTaskRequest { Title = title, Status = TaskItemStatus.Completed });
}

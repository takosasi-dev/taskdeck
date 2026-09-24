using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Data.Export;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Insights;

/// <summary>
/// 完了したタスクを貼れる形の Markdown に（F-108）。日付の振り分けは JST のローカル日付（UTC の日付ではない）。
/// </summary>
public sealed class MarkdownExporterTests : IDisposable
{
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 23, 10, 0);
    private readonly TestDatabase _db;
    private readonly TaskRepository _tasks;
    private readonly MarkdownExporter _exporter;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "taskdeck-md-" + Guid.NewGuid().ToString("N"));

    public MarkdownExporterTests()
    {
        _db = new TestDatabase(_clock);
        _tasks = new TaskRepository(_db, _clock, Substitute.For<IRecurrenceEngine>(), new FakeSettingsStore(), new DataChangeHub(), NullLogger<TaskRepository>.Instance);
        _exporter = new MarkdownExporter(_db, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_Range_GroupsByLocalDateWithCheckboxes()
    {
        await CompleteAtAsync("前日の夜に終えた", 9, 22, 23, 30);    // UTC では 9/22 14:30
        await CompleteAtAsync("日付が変わってすぐ終えた", 9, 23, 0, 10);   // UTC ではまだ 9/22 15:10
        await CompleteAtAsync("朝に終えた", 9, 23, 8, 0);

        var document = await _exporter.BuildAsync(new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23));

        Assert.Equal(3, document.TaskCount);
        Assert.Equal(
            "## 2026-09-22（火）\n\n- [x] 前日の夜に終えた\n\n## 2026-09-23（水）\n\n- [x] 日付が変わってすぐ終えた\n- [x] 朝に終えた\n",
            document.Text);
    }

    [Fact]
    public async Task BuildAsync_Range_IncludesBothEndsLocally()
    {
        await CompleteAtAsync("前の日の終わり", 9, 21, 23, 59);
        await CompleteAtAsync("初日の0時", 9, 22, 0, 0);
        await CompleteAtAsync("最終日の終わり", 9, 23, 23, 59);
        await CompleteAtAsync("次の日の0時", 9, 24, 0, 0);

        var document = await _exporter.BuildAsync(new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23));

        Assert.Equal(2, document.TaskCount);
        Assert.Contains("- [x] 初日の0時", document.Text, StringComparison.Ordinal);
        Assert.Contains("- [x] 最終日の終わり", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("前の日の終わり", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("次の日の0時", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_CancelledDeletedOrOpen_AreLeftOut()
    {
        await CompleteAtAsync("終えた", 9, 23, 9, 0);
        await AddAtAsync("やめた", TaskItemStatus.Cancelled, 9, 23, 9, 0);
        await AddAtAsync("まだ", TaskItemStatus.NotStarted, 9, 23, 9, 0);
        var deleted = await CompleteAtAsync("終えてから消した", 9, 23, 9, 0);
        await _tasks.SoftDeleteAsync([deleted]);

        var document = await _exporter.BuildAsync(new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 23));

        Assert.Equal(1, document.TaskCount);
        Assert.Equal("## 2026-09-23（水）\n\n- [x] 終えた\n", document.Text);
    }

    [Fact]
    public async Task BuildAsync_NothingCompleted_ReturnsEmpty()
    {
        await AddAtAsync("まだ", TaskItemStatus.NotStarted, 9, 23, 9, 0);

        var document = await _exporter.BuildAsync(new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 26));

        Assert.Equal(0, document.TaskCount);
        Assert.Equal("", document.Text);
    }

    [Theory]
    [InlineData(2026, 9, 23, 2026, 9, 23, "TaskDeck_2026-09-23.md")]
    [InlineData(2026, 9, 20, 2026, 9, 26, "TaskDeck_2026-09-20_2026-09-26.md")]
    public void SuggestFileName_Range_StartsWithTaskDeck(int y1, int m1, int d1, int y2, int m2, int d2, string expected) =>
        Assert.Equal(expected, MarkdownExporter.SuggestFileName(new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2)));

    [Fact]
    public async Task WriteAsync_ExistingFile_OverwritesAsUtf8WithoutBomAndLeavesNoTemp()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "TaskDeck_2026-09-23.md");
        await File.WriteAllTextAsync(path, "古い中身がもっと長いファイル");

        await MarkdownExporter.WriteAsync(path, new MarkdownDocument(new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 23), "## 2026-09-23（水）\n\n- [x] 終えた\n", 1));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(0xEF, bytes[0]);   // BOM なし
        Assert.Equal("## 2026-09-23（水）\n\n- [x] 終えた\n", Encoding.UTF8.GetString(bytes));
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    private Task<Guid> CompleteAtAsync(string title, int month, int day, int hour, int minute) =>
        AddAtAsync(title, TaskItemStatus.Completed, month, day, hour, minute);

    /// <summary>JST のその時刻に作る（完了・中止なら、その時刻が閉じた日時になる）。</summary>
    private async Task<Guid> AddAtAsync(string title, TaskItemStatus status, int month, int day, int hour, int minute)
    {
        _clock.UtcNow = FixedClock.LocalToUtc(2026, month, day, hour, minute);
        var result = await _tasks.AddAsync(new NewTaskRequest { Title = title, Status = status });
        return result.Created[0].Id;
    }
}

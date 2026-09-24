using System.Text;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Data.ImportExport;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.ImportExport;

public sealed class CsvExporterTests : IDisposable
{
    private readonly DataWorld _world = new(FixedClock.AtLocal(2026, 9, 22, 10, 0));

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("そのまま", "そのまま")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("1行目\n2行目", "\"1行目\n2行目\"")]
    [InlineData("1行目\r\n2行目", "\"1行目\r\n2行目\"")]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+81 3", "'+81 3")]
    [InlineData("-1", "'-1")]
    [InlineData("@user", "'@user")]
    [InlineData("\tタブ", "'\tタブ")]
    [InlineData("\rCR", "\"'\rCR\"")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData("途中の=は式ではない", "途中の=は式ではない")]
    [InlineData("", "")]
    public void Field_Value_IsQuotedAndDefused(string value, string expected) =>
        Assert.Equal(expected, CsvExporter.Field(value));

    [Fact]
    public async Task Export_NoQuery_WritesAllViewInTreeOrderWithLocalTimes()
    {
        var work = await _world.Projects.AddAsync("業務改善");
        var parent = await _world.AddTaskAsync(new NewTaskRequest
        {
            Title = "親",
            ProjectId = work.Id,
            Priority = Priority.High,
            DueAt = _world.Day(9, 25),
            TagNames = ["会議", "仕事"],
            Notes = "メモ,付き",
        });
        var child = await _world.AddTaskAsync(new NewTaskRequest
        {
            Title = "子",
            ParentTaskId = parent.Id,
            DueAt = _world.At(9, 24, 15, 30),
            DueHasTime = true,
        });
        await _world.AddTaskAsync(new NewTaskRequest { Title = "孫", ParentTaskId = child.Id, Status = TaskItemStatus.Completed });
        await _world.AddTaskAsync(new NewTaskRequest { Title = "=1+1", Priority = Priority.Urgent });
        await _world.AddTaskAsync(new NewTaskRequest { Title = "終わったこと", Status = TaskItemStatus.Completed });   // 「すべて」には出ない

        var (bytes, lines, count) = await ExportAsync(null);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(4, count);
        Assert.Equal(
            [
                "タイトル,階層,状態,優先度,期限,プロジェクト,タグ,メモ,作成日時,完了日時",
                "親,0,未着手,高,2026/09/25,業務改善,仕事 会議,\"メモ,付き\",2026/09/22 10:00,",
                "子,1,未着手,,2026/09/24 15:30,,,,2026/09/22 10:00,",
                "孫,2,完了,,,,,,2026/09/22 10:00,2026/09/22 10:00",
                "'=1+1,0,未着手,緊急,,,,,2026/09/22 10:00,",
            ],
            lines);
    }

    [Fact]
    public async Task Export_GivenQuery_WritesOnlyMatchingRows()
    {
        await _world.AddTaskAsync(new NewTaskRequest { Title = "仕事のもの", TagNames = ["仕事"] });
        await _world.AddTaskAsync(new NewTaskRequest { Title = "ほかのもの" });
        var tag = Assert.Single(await _world.Tags.GetAllAsync());

        var (_, lines, count) = await ExportAsync(new TaskQuery { TagIds = [tag.Id] });

        Assert.Equal(1, count);
        Assert.StartsWith("仕事のもの,0,", lines[1], StringComparison.Ordinal);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task Export_EmptyList_WritesHeaderOnly()
    {
        var (_, lines, count) = await ExportAsync(BuiltInViews.QueryFor(ViewKey.Trash));

        Assert.Equal(0, count);
        Assert.Equal([CsvExporter.Header], lines);
    }

    /// <summary>書き出して、BOM を除いた本文を CRLF で行に分ける（最後の改行の後の空行は除く）。</summary>
    private async Task<(byte[] Bytes, string[] Lines, int Count)> ExportAsync(TaskQuery? query)
    {
        var csv = new CsvExporter(_world.Tasks, _world.Projects, _world.Tags, _world.Clock);
        using var stream = new MemoryStream();
        var count = await csv.ExportAsync(query, stream);
        var bytes = stream.ToArray();
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        return (bytes, text[..^2].Split("\r\n"), count);
    }
}

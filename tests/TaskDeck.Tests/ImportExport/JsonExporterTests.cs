using System.Text;
using System.Text.Json;
using TaskDeck.Data.ImportExport;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.ImportExport;

public sealed class JsonExporterTests : IDisposable
{
    private readonly DataWorld _world = new(FixedClock.AtLocal(2026, 9, 22, 10, 0));

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Export_SeededData_WritesVersionUtcTimesAndNoDeviceState()
    {
        await _world.SeedAsync();

        var bytes = (await _world.ExportAsync()).ToArray();

        Assert.Equal((byte)'{', bytes[0]);   // UTF-8（BOM なし）
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\n  \"formatVersion\": 1,", text, StringComparison.Ordinal);   // インデント付き
        Assert.Contains("\"title\": \"親タスク\"", text, StringComparison.Ordinal);      // 日本語を \u にしない
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal(
            ["formatVersion", "exportedAt", "appVersion", "projects", "tags", "recurrenceRules", "tasks", "taskTags", "templates", "templateItems"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-09-22T01:00:00Z", root.GetProperty("exportedAt").GetString());
        Assert.Equal("0.1.0", root.GetProperty("appVersion").GetString());
        Assert.All(Timestamps(root), value => Assert.EndsWith("Z", value, StringComparison.Ordinal));
        Assert.NotEmpty(Timestamps(root));

        var parent = TaskNamed(root, "親タスク");
        Assert.Equal("2026-09-24T15:00:00Z", parent.GetProperty("dueAt").GetString());   // 日付のみ = JST の 0:00
        Assert.Equal("2026-09-25", parent.GetProperty("dueDate").GetString());
        Assert.Equal("High", parent.GetProperty("priority").GetString());
        var child = TaskNamed(root, "子タスク");
        Assert.Equal("2026-09-24T06:30:00Z", child.GetProperty("dueAt").GetString());
        Assert.False(child.TryGetProperty("dueDate", out _));
        Assert.True(TaskNamed(root, "ゴミ箱のタスク").TryGetProperty("deletedAt", out _));
        Assert.Contains(
            root.GetProperty("templateItems").EnumerateArray(),
            item => item.TryGetProperty("dueTime", out var time) && time.GetString() == "20:00");
        Assert.StartsWith("FREQ=WEEKLY", root.GetProperty("recurrenceRules")[0].GetProperty("rrule").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_Summary_CountsTrashTasksButNotRemovedRows()
    {
        await _world.SeedAsync();

        var summary = await _world.Exporter.ExportAsync(new MemoryStream());

        // タスクはゴミ箱を含む 11 件。やめた案件・消すタグは数えない
        Assert.Equal(new ExportSummary(11, 2, 4, 4), summary);
    }

    private static JsonElement TaskNamed(JsonElement root, string title) =>
        root.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("title").GetString() == title);

    /// <summary>名前が "At" で終わる文字列の値（日時）をすべて。</summary>
    private static List<string> Timestamps(JsonElement element)
    {
        var found = new List<string>();
        Walk(element);
        return found;

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in e.EnumerateObject())
                    {
                        if (property.Name.EndsWith("At", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.String)
                        {
                            found.Add(property.Value.GetString()!);
                        }
                        Walk(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                    {
                        Walk(item);
                    }
                    break;
            }
        }
    }
}

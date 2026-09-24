using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Services;
using TaskDeck.App.Views.DataTools;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Data.ImportExport;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.ImportExport;

public sealed class ImportExportViewModelTests : IDisposable
{
    private readonly DataWorld _world = new(FixedClock.AtLocal(2026, 9, 22, 10, 0));
    private readonly UndoStack _undo = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taskdeck-tests", Guid.NewGuid().ToString("N"));
    private readonly ImportExportViewModel _vm;

    public ImportExportViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        var shell = new ShellService(Substitute.For<IServiceProvider>(), _world.Clock, NullLogger<ShellService>.Instance);
        var csv = new CsvExporter(_world.Tasks, _world.Projects, _world.Tags, _world.Clock);
        _vm = new ImportExportViewModel(_world.Exporter, _world.Importer, csv, shell, _undo, _world.Clock, NullLogger<ImportExportViewModel>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task ImportAsync_Replace_ClearsUndoAndTellsWhereTheBackupIs()
    {
        var package = await ExportAndReadAsync();
        _undo.Push(new UndoEntry("「x」を削除しました", new ChangeSet([], [Guid.NewGuid()], []), UndoKind.Delete));

        await _vm.ImportAsync(package, ImportMode.Replace);

        Assert.Equal(0, _undo.Count);   // 置き換える前のデータに対する取り消しは当てられない
        Assert.False(_vm.IsStatusError);
        Assert.StartsWith("置き換えました（タスク 11 件", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("バックアップ（taskdeck_20260922_100000.db）", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task ImportAsync_Append_KeepsUndoAndReportsMergedNames()
    {
        var package = await ExportAndReadAsync();
        _undo.Push(new UndoEntry("「x」を削除しました", new ChangeSet([], [Guid.NewGuid()], []), UndoKind.Delete));

        await _vm.ImportAsync(package, ImportMode.Append);

        Assert.Equal(1, _undo.Count);
        Assert.Contains("同じ名前のプロジェクト 2 件・タグ 4 件は、今あるものにまとめました。", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_BrokenFile_ShowsReasonAndReturnsNull()
    {
        var path = Path.Combine(_dir, "broken.json");
        await File.WriteAllTextAsync(path, "{ \"formatVersion\": 2 }");

        var package = await _vm.ReadAsync(path);

        Assert.Null(package);
        Assert.True(_vm.IsStatusError);
        Assert.StartsWith("新しい形式（版 2）", _vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportJsonAsync_TargetLocked_ShowsReadableErrorAndLeavesNoTempFile()
    {
        await _world.SeedAsync();
        var path = Path.Combine(_dir, "locked.json");
        await using (var locked = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await _vm.ExportJsonAsync(path);
        }

        Assert.True(_vm.IsStatusError);
        Assert.StartsWith("ファイルに書き込めませんでした。ほかのアプリ（Excel など）で開いていれば閉じてから", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task ExportCsvAsync_NoListShown_WritesAllViewAndReportsRows()
    {
        await _world.SeedAsync();
        var path = Path.Combine(_dir, "list.csv");

        await _vm.ExportCsvAsync(path);

        Assert.False(_vm.IsStatusError);
        Assert.StartsWith("CSV に書き出しました（", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    private async Task<ImportPackage> ExportAndReadAsync()
    {
        await _world.SeedAsync();
        var path = Path.Combine(_dir, "export.json");
        await _vm.ExportJsonAsync(path);
        Assert.False(_vm.IsStatusError, _vm.StatusMessage);
        return (await _vm.ReadAsync(path))!;
    }
}

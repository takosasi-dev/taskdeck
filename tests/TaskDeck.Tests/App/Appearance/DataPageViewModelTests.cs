using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.App;
using TaskDeck.App.Settings.Pages;
using TaskDeck.Data;
using TaskDeck.Data.Backup;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.Appearance;

/// <summary>
/// 設定の「データ」（F-101〜F-103・F-127）。一時フォルダに本物の SQLite ファイルを置き、BackupService を通して確かめる。
/// </summary>
public sealed class DataPageViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "taskdeck-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = FixedClock.AtLocal(2026, 9, 23, 10, 0);
    private readonly AppPaths _paths;
    private readonly BackupService _backups;
    private readonly DataPageViewModel _viewModel;

    public DataPageViewModelTests()
    {
        _paths = new AppPaths(_folder, IsDevelopment: true);
        _paths.EnsureDirectories();
        WriteDatabase(_paths.DatabasePath, "今のデータ");
        _backups = new BackupService(
            new TaskDeckDataOptions(_paths.DatabasePath, _paths.BackupDirectory), _clock, NullLogger<BackupService>.Instance);
        _viewModel = new DataPageViewModel(_paths, _backups, _clock, NullLogger<DataPageViewModel>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Refresh_TwoStartupBackups_ListsNewestFirstWithLocalTime()
    {
        _backups.CreateStartupBackup();
        _clock.Advance(TimeSpan.FromHours(2));
        _backups.CreateStartupBackup();

        _viewModel.Refresh();

        Assert.True(_viewModel.HasBackups);
        Assert.Equal(2, _viewModel.Backups.Count);
        Assert.Equal("2026/09/23 12:00", _viewModel.Backups[0].When);
        Assert.Equal("2026/09/23 10:00", _viewModel.Backups[1].When);
    }

    [Fact]
    public void Refresh_NoBackups_HasBackupsIsFalse()
    {
        _viewModel.Refresh();

        Assert.False(_viewModel.HasBackups);
        Assert.Empty(_viewModel.Backups);
    }

    [Fact]
    public void CreateBackup_ToChosenFile_WritesCopyAndReportsIt()
    {
        var destination = Path.Combine(_folder, "elsewhere", _viewModel.SuggestedBackupName);

        var ok = _viewModel.CreateBackup(destination);

        Assert.True(ok);
        Assert.True(File.Exists(destination));
        Assert.Equal("今のデータ", ReadMarker(destination));
        Assert.Contains(destination, _viewModel.Status, StringComparison.Ordinal);
    }

    /// <summary>手動バックアップを backups に置いても、起動時の世代に数えられて消されないこと。</summary>
    [Fact]
    public void SuggestedBackupName_SavedIntoBackupFolder_IsNotTreatedAsStartupBackup()
    {
        var name = _viewModel.SuggestedBackupName;
        _viewModel.CreateBackup(Path.Combine(_paths.BackupDirectory, name));

        Assert.DoesNotMatch(new Regex(@"^taskdeck_\d{8}_\d{6}(_\d+)?\.db$"), name);
        Assert.Empty(_backups.ListBackups());
    }

    [Fact]
    public void Restore_StartupBackup_ReplacesDatabase()
    {
        _backups.CreateStartupBackup();
        WriteDatabase(_paths.DatabasePath, "あとから変えたデータ");
        _viewModel.Refresh();

        var ok = _viewModel.Restore(_viewModel.Backups[0]);

        Assert.True(ok);
        Assert.Equal("今のデータ", ReadMarker(_paths.DatabasePath));
    }

    [Fact]
    public void Restore_BrokenFile_ReturnsFalseAndKeepsDatabase()
    {
        var broken = Path.Combine(_paths.BackupDirectory, "taskdeck_20260101_000000.db");
        File.WriteAllText(broken, "これは SQLite ではない");

        var ok = _viewModel.Restore(new BackupEntry(broken, "2026/01/01 00:00", "1 KB"));

        Assert.False(ok);
        Assert.StartsWith("復元できませんでした", _viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("今のデータ", ReadMarker(_paths.DatabasePath));
    }

    /// <summary>BackupService.Validate が通る最小の DB（TaskItem 表がある）。中身の目印を1行入れる。</summary>
    private static void WriteDatabase(string path, string marker)
    {
        using var connection = new SqliteConnection(TaskDeckDataOptions.BuildConnectionString(path, pooling: false));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS TaskItem (Title TEXT); DELETE FROM TaskItem; INSERT INTO TaskItem (Title) VALUES ($marker);";
        command.Parameters.AddWithValue("$marker", marker);
        command.ExecuteNonQuery();
    }

    private static string? ReadMarker(string path)
    {
        using var connection = new SqliteConnection(TaskDeckDataOptions.BuildConnectionString(path, pooling: false));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Title FROM TaskItem LIMIT 1;";
        return command.ExecuteScalar() as string;
    }
}

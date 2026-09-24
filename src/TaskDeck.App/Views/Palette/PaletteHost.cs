using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;
using TaskDeck.Data.Backup;

namespace TaskDeck.App.Views.Palette;

/// <summary>
/// パレットからほかの画面や OS に向ける操作（ViewModel が WPF の型を持たず、テストで差し替えられるようにする口）。
/// 画面の出し入れは ShellService（INTERFACES 5.1・5.8）に渡すだけ。
/// </summary>
public interface IPaletteHost
{
    void NavigateTo(ViewKey view);

    void RevealTask(Guid taskId);

    void OpenSettings(string? page);

    void OpenTemplates();

    void OpenFocusMode();

    void OpenShortcuts();

    /// <summary>ライトとダークを入れ替えて、切り替えた後の名前（「ダーク」）を返す。</summary>
    string ToggleTheme();

    /// <summary>手動バックアップを backups フォルダに取り、結果の文を返す（失敗してもログに残して文で返す）。</summary>
    string CreateBackup();

    void OpenDataFolder();
}

/// <summary>本物の <see cref="IPaletteHost"/>。</summary>
public sealed class PaletteHost(
    ShellService shell,
    ThemeService theme,
    ISettingsStore settings,
    BackupService backups,
    AppPaths paths,
    IClock clock,
    ILogger<PaletteHost> logger) : IPaletteHost
{
    public void NavigateTo(ViewKey view) => shell.NavigateTo(view);

    public void RevealTask(Guid taskId) => shell.RevealTask(taskId);

    public void OpenSettings(string? page) => shell.OpenSettings(page);

    public void OpenTemplates() => shell.OpenTemplates();

    public void OpenFocusMode() => shell.OpenFocusMode();

    public void OpenShortcuts() => shell.OpenShortcuts();

    public string ToggleTheme()
    {
        var next = theme.IsDark ? AppThemeMode.Light : AppThemeMode.Dark;
        settings.Update(s => s.Appearance.Theme = next);
        theme.ApplyFromSettings();
        return next == AppThemeMode.Dark ? "ダーク" : "ライト";
    }

    /// <summary>
    /// 名前は設定画面の手動バックアップと同じ形（taskdeck_manual_…）。起動時の世代（taskdeck_yyyyMMdd_HHmmss）と
    /// 違う名前なので、古い順に消される対象にならない。
    /// </summary>
    public string CreateBackup()
    {
        var name = "taskdeck_manual_" + clock.LocalNow().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".db";
        try
        {
            backups.CreateBackup(Path.Combine(paths.BackupDirectory, name));
            return $"バックアップを作りました（backups\\{name}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            logger.LogError(ex, "パレットからのバックアップに失敗しました");
            return "バックアップを作れませんでした（ログに記録しました）";
        }
    }

    public void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{paths.DataDirectory}\"") { UseShellExecute = true })?.Dispose();
}

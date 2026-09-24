using System.Windows;
using Microsoft.Extensions.Hosting;
using TaskDeck.App.Services;

namespace TaskDeck.App.Settings;

/// <summary>
/// 開発時だけ、起動直後に設定画面（またはピッカーの確認窓）を開く。担当: 波1-D。
/// 画面の確認とスクリーンショットのための入口で、本番では何もしない。
///
/// 使い方:
///   $env:TASKDECK_DATA_DIR = "...\.devdata\d2"    ← これが無いと AppPaths.IsDevelopment が false で何もしない
///   $env:TASKDECK_DEV_OPEN = "appearance"         ← 設定のページ名（SettingsWindow.PageNames）か "pickers"
/// 知らない値では何もしない（F は TASKDECK_DEV_TEMPLATES、G は TASKDECK_DEV_DATATOOLS で自分の窓を開くので、ぶつからない）。
/// </summary>
internal sealed class DevSettingsLauncher(AppPaths paths, ShellService shell) : IHostedService
{
    public const string Variable = "TASKDECK_DEV_OPEN";
    public const string PickersTarget = "pickers";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var target = Environment.GetEnvironmentVariable(Variable)?.Trim();
        if (!paths.IsDevelopment || string.IsNullOrEmpty(target))
        {
            return Task.CompletedTask;
        }
        var isPickers = string.Equals(target, PickersTarget, StringComparison.OrdinalIgnoreCase);
        if (!isPickers && !SettingsWindow.PageNames.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }
        // メイン画面が出たあとに開く（BeginInvoke で起動処理のあとに回す）
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (isPickers)
            {
                new DevPickerPreviewWindow { Owner = Application.Current.MainWindow }.Show();
                return;
            }
            shell.OpenSettings(target);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

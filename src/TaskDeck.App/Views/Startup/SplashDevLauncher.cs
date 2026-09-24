using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaskDeck.App.Views.Startup;

/// <summary>
/// 起動画面の確認用の入口（INTERFACES 5.10。開発用データフォルダのときだけ働く。本番では登録もしない）。
/// 開発用の小さな DB だと起動画面はすぐ消えるので、起動が落ち着いてから、動きの途中と動き終わり（等倍と 150%）・動きを減らす設定の絵を
/// &lt;データフォルダ&gt;\dev-splash\ に書き出し、本物の起動画面を指定の秒数だけ出して閉じる。
/// TASKDECK_DEV_SPLASH=秒数（出しておく長さ。既定 3）／reduce（動きを減らす設定で出す）
/// </summary>
internal sealed class SplashDevLauncher(AppPaths paths, ILogger<SplashDevLauncher> logger) : IHostedService
{
    public const string Variable = "TASKDECK_DEV_SPLASH";

    public static bool IsRequested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (paths.IsDevelopment)
        {
            // StartAsync は画面を出す前に呼ばれるので、メイン画面（か初回起動）が出てから出す
            _ = Application.Current.Dispatcher.InvokeAsync(ShowAsync, DispatcherPriority.ApplicationIdle);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ShowAsync()
    {
        var value = Environment.GetEnvironmentVariable(Variable)?.Trim() ?? "";
        var reduce = value.Equals("reduce", StringComparison.OrdinalIgnoreCase);
        var seconds = int.TryParse(value, out var n) && n > 0 ? n : 3;
        var highContrast = SystemParameters.HighContrast;

        var folder = Path.Combine(paths.DataDirectory, "dev-splash");
        Directory.CreateDirectory(folder);
        foreach (var ms in new double[] { 50, 150, 250, 325, 400 })
        {
            StartupSplash.SaveFrame(Path.Combine(folder, $"splash-{ms:000}ms.png"), ms, 1, highContrast);
        }
        StartupSplash.SaveFrame(Path.Combine(folder, "splash-end.png"), 1000, 1, highContrast);
        StartupSplash.SaveFrame(Path.Combine(folder, "splash-end-150.png"), 1000, 1.5, highContrast);
        StartupSplash.SaveFrame(Path.Combine(folder, "splash-reduce.png"), null, 1, highContrast);
        logger.LogInformation("確認用の絵を書き出しました: {Folder}", folder);

        var splash = StartupSplash.Show(reduce, highContrast);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        splash.Close();
        logger.LogInformation("確認用の起動画面を閉じました（{Seconds} 秒、動きを減らす: {Reduce}）", seconds, reduce);
    }
}

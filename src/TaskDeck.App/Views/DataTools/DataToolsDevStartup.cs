using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TaskDeck.App.Views.DataTools;

/// <summary>
/// 開発用: 起動の後に確認用の窓（<see cref="DataToolsDevWindow"/>）を開く。
/// 開発用データフォルダ（TASKDECK_DATA_DIR）で起動し、さらに環境変数 TASKDECK_DEV_DATATOOLS があるときだけ。本番では何もしない。
/// </summary>
internal sealed class DataToolsDevStartup(AppPaths paths, IServiceProvider services) : IHostedService
{
    public const string Variable = "TASKDECK_DEV_DATATOOLS";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (paths.IsDevelopment && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            // 起動処理（DB の準備・メイン画面の表示）が済んでから開く
            Application.Current.Dispatcher.BeginInvoke(() => services.GetRequiredService<DataToolsDevWindow>().Show());
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

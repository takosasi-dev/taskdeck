using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using TaskDeck.App.Services;

namespace TaskDeck.App.Views.Templates;

/// <summary>
/// 開発時だけの確認用の入口。メイン画面からテンプレート画面を開けるようになるまで（統合前）の確かめ用で、
/// 開発用データフォルダ（TASKDECK_DATA_DIR）かつ TASKDECK_DEV_TEMPLATES があるときだけ働く。本番では何もしない。
/// </summary>
internal static class TemplatesDevTools
{
    public const string Variable = "TASKDECK_DEV_TEMPLATES";

    public static bool IsRequested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    public static bool IsEnabled(AppPaths paths) => paths.IsDevelopment && IsRequested;
}

/// <summary>起動の後（メイン画面が出てから）にテンプレート画面を開く。</summary>
internal sealed class TemplatesDevLauncher(AppPaths paths, ShellService shell) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (TemplatesDevTools.IsEnabled(paths))
        {
            // StartAsync は画面を出す前に呼ばれるので、起動処理が終わってから開く
            Application.Current.Dispatcher.InvokeAsync(shell.OpenTemplates, DispatcherPriority.ApplicationIdle);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

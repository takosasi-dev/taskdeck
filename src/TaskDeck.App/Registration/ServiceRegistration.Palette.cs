using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Focus;
using TaskDeck.App.Views.Palette;
using TaskDeck.App.Views.Shortcuts;

namespace TaskDeck.App.Registration;

// 担当: 波3-J（コマンドパレット・ショートカット一覧・フォーカスモード）
internal static partial class ServiceRegistration
{
    static partial void AddPalette(IServiceCollection services)
    {
        services.AddSingleton<IPaletteHost, PaletteHost>();
        services.AddTransient<CommandPaletteViewModel>();
        services.AddTransient<CommandPaletteWindow>();
        services.AddTransient<ShortcutsViewModel>();
        services.AddTransient<ShortcutsWindow>();
        services.AddTransient<FocusViewModel>();
        services.AddTransient<FocusWindow>();
        if (PaletteDevLauncher.IsRequested)
        {
            // 確認用の入口（開発用データフォルダのときだけ、起動後にパレットなどを開く。本番では登録もしない）
            services.AddHostedService<PaletteDevLauncher>();
        }
    }
}

/// <summary>
/// 開発時だけの確認用の入口（INTERFACES 5.8）。開発用データフォルダ（TASKDECK_DATA_DIR）かつ
/// TASKDECK_DEV_PALETTE=palette / shortcuts / focus のとき、起動処理が済んでから該当の画面を開く。本番では何もしない。
/// </summary>
internal sealed class PaletteDevLauncher(AppPaths paths, ShellService shell) : IHostedService
{
    public const string Variable = "TASKDECK_DEV_PALETTE";

    public static bool IsRequested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!paths.IsDevelopment)
        {
            return Task.CompletedTask;
        }
        Action? open = Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant() switch
        {
            "palette" => shell.OpenCommandPalette,
            "shortcuts" => shell.OpenShortcuts,
            "focus" => shell.OpenFocusMode,
            _ => null,
        };
        if (open is not null)
        {
            // StartAsync は画面を出す前に呼ばれるので、メイン画面が出てから開く
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, open);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

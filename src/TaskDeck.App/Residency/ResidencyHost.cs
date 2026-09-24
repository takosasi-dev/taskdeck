using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskDeck.App.Services;
using TaskDeck.App.Views.QuickInput;

namespace TaskDeck.App.Residency;

/// <summary>
/// 開発用フォルダでの決まり（INTERFACES 5.9）。本番（開発用フォルダでない）ではすべて本物を使う。
/// - 自動起動（HKCU の Run）: 開発用フォルダでは絶対に書かない
/// - グローバルホットキー: 開発用フォルダでは TASKDECK_DEV_HOTKEYS=1 のときだけ登録する
/// - Windows の通知（トースト）: 開発用フォルダでは TASKDECK_DEV_TOASTS=1 のときだけ使う（AUMID・COM の登録もこのときだけ）
/// - 窓を前に出さない: 開発用フォルダで TASKDECK_DEV_NOACTIVATE=1 のとき（ShellService と同じ判定）
/// </summary>
public sealed record ResidencySwitches(bool IsDevelopment, bool RegisterHotkeys, bool ShowToasts, bool NoActivate)
{
    public bool WriteRunKey => !IsDevelopment;

    public static ResidencySwitches From(AppPaths paths) => paths.IsDevelopment
        ? new ResidencySwitches(true, Flag("TASKDECK_DEV_HOTKEYS"), Flag("TASKDECK_DEV_TOASTS"), Flag("TASKDECK_DEV_NOACTIVATE"))
        : new ResidencySwitches(false, RegisterHotkeys: true, ShowToasts: true, NoActivate: false);

    private static bool Flag(string name) => Environment.GetEnvironmentVariable(name) == "1";
}

/// <summary>
/// 常駐の組み込み（INTERFACES 5.9）。App.xaml.cs の順で ShellService.StartUp より前に走るので、ここでトレイを作って
/// IsTrayAvailable を立てる（× と Esc の最後の段がトレイへ隠すに変わり、--tray と -ToastActivated の起動で窓を出さなくなる）。
/// ホットキー・自動起動・通知の受け口もここでつなぐ。後片付けは DI の破棄（App.OnExit の UI スレッド）で各サービスが行う
/// （StopAsync は UI スレッドとは限らないので、ここでは窓に触らない）。
/// </summary>
internal sealed class ResidencyHost(
    IServiceProvider services,
    ShellService shell,
    TrayIconService tray,
    HotkeyService hotkeys,
    AutoStartService autoStart,
    ToastNotifier toasts) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            StartOnUiThread();
        }
        else
        {
            await dispatcher.InvokeAsync(StartOnUiThread);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void StartOnUiThread()
    {
        // トレイがあることは先に決めておく（× と Esc の最後の段・窓を出さない起動の判定に使う）。トレイを作るのと、通知・ホットキー・自動起動の登録は、
        // 手が空いてから（メイン画面が出るのを遅らせないため。NFR 3.2 の起動2秒。窓を出さない起動ではすぐ空く。通知の見回りは起動の3秒後から）
        shell.IsTrayAvailable = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            tray.Create();
            toasts.Start();
            hotkeys.Pressed += (_, action) => OnHotkey(action);
            hotkeys.Start();
            autoStart.Start();
            // 最初のホットキーでも 150ms 以内に出せるよう、クイック入力窓もこの後の空き時間に作っておく（NFR 3.2）
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => services.GetRequiredService<QuickInputWindow>().Prepare());
        });
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.QuickInput:
                QuickInputWindow.MarkRequested();
                shell.OpenQuickInput();
                break;
            case HotkeyAction.ShowMainWindow:
                shell.ShowMainWindow();
                break;
            case HotkeyAction.FocusMode:
                shell.OpenFocusMode();
                break;
        }
    }
}

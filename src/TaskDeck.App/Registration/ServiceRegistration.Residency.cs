using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.BackgroundTasks;
using TaskDeck.App.Residency;
using TaskDeck.App.Services;
using TaskDeck.App.Views.QuickInput;

namespace TaskDeck.App.Registration;

// 担当: 波3-H（トレイ・ホットキー・クイック入力・通知・自動起動）
internal static partial class ServiceRegistration
{
    static partial void AddResidency(IServiceCollection services)
    {
        // 開発用フォルダでの決まり（ホットキー・トースト・自動起動を本物にするか）。INTERFACES 5.9
        services.AddSingleton(sp => ResidencySwitches.From(sp.GetRequiredService<AppPaths>()));

        services.AddSingleton<IHotkeyApi, HwndHotkeyApi>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<IRunKey, RegistryRunKey>();
        services.AddSingleton<AutoStartService>();

        services.AddSingleton(sp =>
        {
            var shell = sp.GetRequiredService<ShellService>();
            return new TrayActions(
                Open: shell.ShowMainWindow,
                QuickAdd: () =>
                {
                    QuickInputWindow.MarkRequested();
                    shell.OpenQuickInput();
                },
                OpenSettings: () => shell.OpenSettings(),
                Exit: shell.ExitApplication);
        });
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<TrayIconService>();

        services.AddSingleton<ReminderActions>();
        services.AddSingleton<ToastNotifier>();
        services.AddSingleton<IReminderNotifier>(sp => sp.GetRequiredService<ToastNotifier>());

        // クイック入力の窓は1つを使い回す（閉じずに隠す。2回目からは作り直さないので速く出る）
        services.AddSingleton<QuickInputViewModel>();
        services.AddSingleton<QuickInputWindow>();

        // 起動の順: トレイ・ホットキー・自動起動・通知の受け口 → 通知の見回り → 開発用の確認の入口（本番では何もしない）
        services.AddHostedService<ResidencyHost>();
        services.AddHostedService<ReminderWorker>();
        services.AddHostedService<ResidencyDevTools>();
    }
}

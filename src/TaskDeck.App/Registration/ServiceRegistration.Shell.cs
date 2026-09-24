using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views;

namespace TaskDeck.App.Registration;

// 担当: 波1-C（メイン画面）
internal static partial class ServiceRegistration
{
    static partial void AddShell(IServiceCollection services)
    {
        // UI スレッドへ戻す手段（DataChangeHub や UndoService は別スレッドから発火する）
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Application.Current.Dispatcher));

        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<TaskListViewModel>();
        services.AddSingleton<TaskDetailViewModel>();
        services.AddSingleton<ToastViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    /// <summary>WPF の Dispatcher に載せる IUiDispatcher（ViewModel が WPF の型を持たないための橋渡し）。</summary>
    private sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public void Post(Action action) => dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }
}

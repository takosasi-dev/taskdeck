using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.Views.Calendar;

namespace TaskDeck.App.Registration;

// 担当: 波3-I（カレンダー）
internal static partial class ServiceRegistration
{
    static partial void AddCalendar(IServiceCollection services)
    {
        // メイン画面に1つだけ置く CalendarView が AppServices から取る
        services.AddSingleton<CalendarViewModel>();
    }
}

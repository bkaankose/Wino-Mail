using Microsoft.Extensions.DependencyInjection;
using Wino.Core;

namespace Wino.Calendar.ViewModels
{
    public static class CalendarViewModelContainerSetup
    {
        public static void RegisterCalendarViewModelServices(this IServiceCollection services)
        {
            services.RegisterCoreServices();
        }
    }
}

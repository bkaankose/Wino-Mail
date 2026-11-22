using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Calendar;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterCalendar(IServiceCollection services)
    {
        CalendarSidebarEntryPoints.Register();
    }
}

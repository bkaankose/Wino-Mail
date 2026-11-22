using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Contacts;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterContacts(IServiceCollection services)
    {
        ContactsSidebarMenus.Register();
    }
}

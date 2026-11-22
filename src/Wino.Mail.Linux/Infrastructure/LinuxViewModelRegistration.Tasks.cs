using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.ToDo;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterTasks(IServiceCollection services)
    {
        ToDoSidebarMenus.Register();
    }
}

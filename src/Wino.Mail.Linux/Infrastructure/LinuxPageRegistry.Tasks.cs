using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.ToDo;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterTasksPages()
    {
        Register<ToDoPageView>(WinoPage.ToDoPage, LinuxPageHost.ShellContent);
    }
}

using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.ToDo;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>To Do mode routes. Owned by the Tasks feature.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterTasksPages()
    {
        Register<ToDoPageViewController>(WinoPage.ToDoPage, MacPageHost.ShellContent);
    }
}

using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.ToDo;

namespace Wino.Mail.UIKit.Phone;

public sealed partial class PhonePageRegistry
{
    partial void RegisterTasksPages();

    /// <summary>To Do: the task page is the mode's shell content; it places its own detail (pushed) and slides its Lists root under itself.</summary>
    partial void RegisterTasksPages() => Register<PhoneToDoPageViewController>(WinoPage.ToDoPage, UIKitPageHost.ShellContent);
}

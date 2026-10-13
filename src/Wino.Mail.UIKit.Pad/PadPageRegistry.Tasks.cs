using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.ToDo;

namespace Wino.Mail.UIKit.Pad;

public sealed partial class PadPageRegistry
{
    partial void RegisterTasksPages();

    /// <summary>To Do: the task page is the mode's shell content; it places its own detail drawer in the reader column.</summary>
    partial void RegisterTasksPages() => Register<PadToDoPageViewController>(WinoPage.ToDoPage, UIKitPageHost.ShellContent);
}

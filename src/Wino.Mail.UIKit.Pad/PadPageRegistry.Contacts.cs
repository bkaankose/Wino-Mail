using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Contacts;
using Wino.Mail.UIKit.Views.Contacts;

namespace Wino.Mail.UIKit.Pad;

public sealed partial class PadPageRegistry
{
    partial void RegisterContactsPages();

    /// <summary>
    /// Contacts: the list is the mode's shell content; the editor (new and edit) is a form sheet, because the shared editor
    /// leaves through the router's back, which only a sheet can absorb (the router closes a single-page sheet on back).
    /// </summary>
    partial void RegisterContactsPages()
    {
        Register<PadContactsPageViewController>(WinoPage.ContactsPage, UIKitPageHost.ShellContent);
        Register<ContactEditPageViewController>(WinoPage.ContactEditPage, UIKitPageHost.Sheet);
    }
}

using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Contacts;
using Wino.Mail.UIKit.Views.Contacts;

namespace Wino.Mail.UIKit.Phone;

public sealed partial class PhonePageRegistry
{
    partial void RegisterContactsPages();

    /// <summary>
    /// Contacts: the list is the mode's shell content; the editor (new and edit) is a full-screen sheet, because the shared editor
    /// leaves through the router's back, which only a sheet can absorb (the router closes a single-page sheet on back).
    /// </summary>
    partial void RegisterContactsPages()
    {
        Register<PhoneContactsPageViewController>(WinoPage.ContactsPage, UIKitPageHost.ShellContent);
        Register<ContactEditPageViewController>(WinoPage.ContactEditPage, UIKitPageHost.FullScreenSheet);
    }
}

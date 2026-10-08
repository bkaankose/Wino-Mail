using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Contacts;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Contacts mode routes. Owned by the Contacts feature.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterContactsPages()
    {
        Register<ContactsPageViewController>(WinoPage.ContactsPage, MacPageHost.ShellContent);
        // The editor replaces the list in the content zone, as the Windows frame does; Save and Cancel go back.
        Register<ContactEditPageViewController>(WinoPage.ContactEditPage, MacPageHost.ShellContent);
    }
}

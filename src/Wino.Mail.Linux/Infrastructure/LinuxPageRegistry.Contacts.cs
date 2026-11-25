using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Contacts;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterContactsPages()
    {
        Register<ContactsPageView>(WinoPage.ContactsPage, LinuxPageHost.ShellContent);
        Register<ContactEditView>(WinoPage.ContactEditPage, LinuxPageHost.ShellContent);
    }
}

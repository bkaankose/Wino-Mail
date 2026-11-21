using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsModesPages()
    {
        Register<StoragePage>(WinoPage.StoragePage, LinuxPageHost.Settings);
        Register<AboutPage>(WinoPage.AboutPage, LinuxPageHost.Settings);
        Register<ContactsPreferenceSettingsPage>(WinoPage.ContactsPreferenceSettingsPage, LinuxPageHost.Settings);
        Register<ToDoPreferenceSettingsPage>(WinoPage.ToDoPreferenceSettingsPage, LinuxPageHost.Settings);
    }
}

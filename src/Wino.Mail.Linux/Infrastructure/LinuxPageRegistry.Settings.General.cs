using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsGeneralPages()
    {
        Register<AppPreferencesPage>(WinoPage.AppPreferencesPage, LinuxPageHost.Settings);
        Register<MailPreferencesPage>(WinoPage.MailPreferencesPage, LinuxPageHost.Settings);
        Register<MessageListPage>(WinoPage.MessageListPage, LinuxPageHost.Settings);
    }
}

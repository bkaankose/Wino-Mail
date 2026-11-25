using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsNotificationsPages()
    {
        Register<NotificationSettingsPage>(WinoPage.NotificationSettingsPage, LinuxPageHost.Settings);
        Register<UnreadBadgeSettingsPage>(WinoPage.UnreadBadgeSettingsPage, LinuxPageHost.Settings);
        Register<AccountUnreadBadgePage>(WinoPage.AccountUnreadBadgePage, LinuxPageHost.Settings);
    }
}

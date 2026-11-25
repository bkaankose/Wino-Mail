using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsMailPages()
    {
        Register<ReadComposePanePage>(WinoPage.ReadComposePanePage, LinuxPageHost.Settings);
        Register<ReaderActionsPage>(WinoPage.ReaderActionsPage, LinuxPageHost.Settings);
        Register<PrivacySecurityPage>(WinoPage.PrivacySecurityPage, LinuxPageHost.Settings);
        Register<RemoteContentRulesPage>(WinoPage.RemoteContentRulesPage, LinuxPageHost.Settings);
    }
}

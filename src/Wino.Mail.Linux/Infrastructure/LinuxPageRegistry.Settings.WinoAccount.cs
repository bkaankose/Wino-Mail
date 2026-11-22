using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings.WinoAccount;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsWinoAccountPages() => Register<WinoAccountPage>(WinoPage.WinoAccountManagementPage, LinuxPageHost.Settings);
}

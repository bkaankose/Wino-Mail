using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsAccountsPages()
    {
        Register<ManageAccountsPage>(WinoPage.ManageAccountsPage, LinuxPageHost.Settings);
        Register<ManageAccountsPage>(WinoPage.AccountManagementPage, LinuxPageHost.Settings);
        Register<AccountGroupDetailsPage>(WinoPage.MergedAccountDetailsPage, LinuxPageHost.Settings);
        Register<AccountDetailsPage>(WinoPage.AccountDetailsPage, LinuxPageHost.Settings);
    }
}

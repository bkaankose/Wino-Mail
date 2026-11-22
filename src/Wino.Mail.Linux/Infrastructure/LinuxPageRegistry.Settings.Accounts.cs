using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;
using Wino.Mail.Linux.Views.Settings.Accounts;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsAccountsPages()
    {
        Register<ManageAccountsPage>(WinoPage.ManageAccountsPage, LinuxPageHost.Settings);
        Register<ManageAccountsPage>(WinoPage.AccountManagementPage, LinuxPageHost.Settings);
        Register<AccountGroupDetailsPage>(WinoPage.MergedAccountDetailsPage, LinuxPageHost.Settings);
        Register<AccountHubPage>(WinoPage.AccountDetailsPage, LinuxPageHost.Settings);
        Register<AccountMailSettingsPage>(WinoPage.AccountMailSettingsPage, LinuxPageHost.Settings);
        Register<AccountCalendarSettingsPage>(WinoPage.AccountCalendarSettingsPage, LinuxPageHost.Settings);
        Register<AccountPeopleSettingsPage>(WinoPage.AccountPeopleSettingsPage, LinuxPageHost.Settings);
        Register<AccountToDoSettingsPage>(WinoPage.AccountToDoSettingsPage, LinuxPageHost.Settings);
        Register<AccountSignInPage>(WinoPage.AccountSignInPage, LinuxPageHost.Settings);
        Register<AccountTroubleshootingPage>(WinoPage.AccountTroubleshootingPage, LinuxPageHost.Settings);
        Register<AliasManagementPage>(WinoPage.AliasManagementPage, LinuxPageHost.Settings);
    }
}

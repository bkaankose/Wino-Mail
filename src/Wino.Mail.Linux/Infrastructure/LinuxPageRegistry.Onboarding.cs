using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Onboarding;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    // As on macOS: the welcome wizard and Settings > Add account both open the setup pages in the onboarding window.
    partial void RegisterOnboardingPages()
    {
        Register<AccountSetupView>(WinoPage.AccountSetupPage, LinuxPageHost.Window);
        // Account details edits server settings inside Settings; adding an account stays in the onboarding window.
        Register<ImapCalDavSettingsView>(WinoPage.ImapCalDavSettingsPage, LinuxPageHost.Settings);
        RegisterHostResolver(WinoPage.ImapCalDavSettingsPage, parameter =>
            parameter is ImapCalDavSettingsNavigationContext { Mode: not ImapCalDavSettingsPageMode.Edit } ? LinuxPageHost.Window : null);
    }
}

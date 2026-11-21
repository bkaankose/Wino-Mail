using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Onboarding;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    // As on macOS: the welcome wizard and Settings > Add account both open the setup pages in the onboarding window.
    partial void RegisterOnboardingPages()
    {
        Register<AccountSetupView>(WinoPage.AccountSetupPage, LinuxPageHost.Window);
    }
}

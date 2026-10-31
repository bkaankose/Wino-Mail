using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// The page every Add account entry point opens. Heads that render the address-first setup page opt in through
/// <see cref="IPlatformCapabilities.AddressFirstAccountSetup"/>; the others keep the provider page.
/// </summary>
public static class AccountSetupNavigation
{
    public static WinoPage GetAddAccountPage(IPlatformCapabilities? platformCapabilities)
        => platformCapabilities?.AddressFirstAccountSetup == true
            ? WinoPage.AccountSetupPage
            : WinoPage.ProviderSelectionPage;
}

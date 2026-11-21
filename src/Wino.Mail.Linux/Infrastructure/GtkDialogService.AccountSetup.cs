using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    // No shared code calls it: every Add account entry point opens the address-first setup page (AddressFirstAccountSetup).
    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders)
        => Task.FromException<AccountCreationDialogResult>(new NotSupportedException(
            Dismiss(nameof(ShowAccountProviderSelectionDialogAsync), null, "Account provider selection has no screen on Linux; the setup page replaces it.")));
}

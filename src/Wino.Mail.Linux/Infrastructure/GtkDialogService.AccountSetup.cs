using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    // No shared code calls it: every Add account entry point opens the address-first setup page (AddressFirstAccountSetup).
    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders)
        => Task.FromException<AccountCreationDialogResult>(new NotSupportedException(
            Dismiss(nameof(ShowAccountProviderSelectionDialogAsync), null, "Account provider selection has no screen on Linux; the setup page replaces it.")));

    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult)
        => new AccountCreationDialog(dispatcher, ResolveOwner);

    public async Task ShowImapValidationFailedDialogAsync(string errorMessage, string protocolLog)
    {
        AlertRequest? request = null;
        await dispatcher.ExecuteOnUIThread(() => request = ImapValidationFailedDialog.Create(errorMessage, protocolLog));
        if (request is null || await AlertAsync(request) != ImapValidationFailedDialog.Copy) return;
        if (services.GetService<IClipboardService>() is { } clipboard)
            (await clipboard.CopyTextAsync($"{errorMessage}{Environment.NewLine}{Environment.NewLine}{protocolLog}")).ThrowIfNotSucceeded();
    }
}

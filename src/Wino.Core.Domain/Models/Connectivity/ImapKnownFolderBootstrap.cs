using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Connectivity;

public static class ImapKnownFolderBootstrap
{
    public static ImapKnownFolderBootstrapState GetInitialState(MailProviderType providerType, bool isMailAccessGranted)
        => providerType == MailProviderType.IMAP4 && isMailAccessGranted
            ? ImapKnownFolderBootstrapState.Pending
            : ImapKnownFolderBootstrapState.NotRequired;

    public static async Task CompleteAsync(MailAccount account, Func<MailAccount, Task> persistAsync)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(persistAsync);

        if (account.ImapKnownFolderBootstrapState != ImapKnownFolderBootstrapState.Pending)
            return;

        account.ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Completed;
        try
        {
            await persistAsync(account).ConfigureAwait(false);
        }
        catch
        {
            account.ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Pending;
            throw;
        }
    }
}

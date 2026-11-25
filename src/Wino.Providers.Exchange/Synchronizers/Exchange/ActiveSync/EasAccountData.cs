using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>What the server can ask the client to do with the account's local data, carried out through the account services.</summary>
internal sealed class EasAccountData(IAccountService accounts, MailAccount account)
{
    /// <summary>Drops the synchronized mail and every ActiveSync sync key; folder rows stay, so their settings survive the resync.</summary>
    internal async Task DiscardSynchronizationStateAsync()
    {
        await accounts.ResetExchangeTransportStateAsync(account.Id).ConfigureAwait(false);
        account.SynchronizationDeltaIdentifier = string.Empty;
    }

    internal async Task EraseForRemoteWipeAsync()
    {
        await accounts.EraseAccountDataForRemoteWipeAsync(account.Id).ConfigureAwait(false);
        account.AttentionReason = AccountAttentionReason.RemovedByAdministrator;
    }
}

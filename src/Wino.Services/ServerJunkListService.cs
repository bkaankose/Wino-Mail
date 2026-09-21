using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Orchestrates a Safe/Blocked list edit onto the account's server through its synchronizer: no
/// server list, no synchronizer, or a failed write all come back as false and a log line, never an
/// exception, so the local edit stands.
/// </summary>
public class ServerJunkListService : IServerJunkListService
{
    private readonly ISynchronizerFactory _synchronizerFactory;

    public ServerJunkListService(ISynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    public async Task<bool> SupportsServerJunkListsAsync(MailAccount account)
    {
        if (account is null)
            return false;

        try
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(account.Id).ConfigureAwait(false);
            return synchronizer is { SupportsServerJunkLists: true };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Server junk list support could not be determined for account {AccountId}.", account.Id);
            return false;
        }
    }

    public async Task<bool> TryUpdateAsync(Guid accountId, string address, JunkListType listType, bool add, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        try
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(accountId).ConfigureAwait(false);
            if (synchronizer is not { SupportsServerJunkLists: true })
                return false;

            await synchronizer.UpdateServerJunkListAsync(address, listType, add, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Server junk list update failed for account {AccountId} ({List}, add={Add}); the local list keeps the change.", accountId, listType, add);
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Thin, read-only orchestrator over the account's synchronizer for Global Address List (directory) lookups
/// that feed recipient autocomplete. Mirrors <see cref="ServerJunkListService"/> and degrades gracefully:
/// returns an empty list (never throws, except for cancellation) for non-Exchange accounts, a missing
/// synchronizer, or any directory error, so suggestions fall back to the user's local contacts.
/// </summary>
public class GlobalAddressListService : IGlobalAddressListService
{
    private readonly ISynchronizerFactory _synchronizerFactory;

    public GlobalAddressListService(ISynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    public bool SupportsGlobalAddressList(MailAccount account) => account?.ProviderType == MailProviderType.Exchange;

    public async Task<IReadOnlyList<AccountContact>> SearchAsync(Guid accountId, string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Array.Empty<AccountContact>();

        try
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(accountId).ConfigureAwait(false);
            if (synchronizer is not { SupportsGlobalAddressList: true })
                return Array.Empty<AccountContact>();

            return await synchronizer.SearchGlobalAddressListAsync(query, maxResults, cancellationToken).ConfigureAwait(false) ?? Array.Empty<AccountContact>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GAL search failed for account {AccountId}; falling back to local contacts.", accountId);
            return [];
        }
    }
}

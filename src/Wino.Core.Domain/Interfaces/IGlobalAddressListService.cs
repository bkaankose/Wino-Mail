using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Read-only directory (Global Address List) lookup over the account's synchronizer, used to feed recipient
/// autocomplete. Exchange-only; returns an empty list for other providers or on error so callers degrade
/// gracefully to local-only suggestions. Results are transient <see cref="AccountContact"/> entries (never persisted).
/// </summary>
public interface IGlobalAddressListService
{
    /// <summary>Whether the account exposes a Global Address List (currently Exchange only).</summary>
    bool SupportsGlobalAddressList(MailAccount account);

    /// <summary>
    /// Searches the account's GAL for <paramref name="query"/>, returning up to <paramref name="maxResults"/>
    /// transient contacts. Returns an empty list for non-Exchange accounts, an unavailable synchronizer, or any
    /// directory error. Cancellation propagates so a superseded suggestion query stops promptly.
    /// </summary>
    Task<IReadOnlyList<AccountContact>> SearchAsync(Guid accountId, string query, int maxResults, CancellationToken cancellationToken = default);
}

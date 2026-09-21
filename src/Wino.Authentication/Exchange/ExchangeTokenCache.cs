using System;
using System.Collections.Concurrent;
using System.Threading;
using Wino.Authentication.Oidc;

namespace Wino.Authentication.Exchange;

/// <summary>In-memory OAuth access-token cache keyed by account id.</summary>
public sealed class ExchangeTokenCache
{
    private readonly ConcurrentDictionary<Guid, OidcTokenSet> _tokens = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    public bool TryGet(Guid accountId, out OidcTokenSet tokenSet) => _tokens.TryGetValue(accountId, out tokenSet);

    public void Set(Guid accountId, OidcTokenSet tokenSet) => _tokens[accountId] = tokenSet;

    public void Remove(Guid accountId) => _tokens.TryRemove(accountId, out _);

    /// <summary>
    /// Per-account gate that serializes token refresh so concurrent syncs (mail plus push notifications) don't
    /// both spend the same refresh token; single-use rotation on the server would otherwise reject the second
    /// and drop a token.
    /// </summary>
    public SemaphoreSlim GetRefreshLock(Guid accountId) => _refreshLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
}

using System;
using System.Collections.Concurrent;
using Wino.Core.Authentication;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>In-memory OAuth access-token cache keyed by account id.</summary>
public sealed class ExchangeTokenCache
{
    private readonly ConcurrentDictionary<Guid, OidcTokenSet> _tokens = new();

    public bool TryGet(Guid accountId, out OidcTokenSet tokenSet) => _tokens.TryGetValue(accountId, out tokenSet);

    public void Set(Guid accountId, OidcTokenSet tokenSet) => _tokens[accountId] = tokenSet;

    public void Remove(Guid accountId) => _tokens.TryRemove(accountId, out _);
}

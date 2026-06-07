using System;
using System.Collections.Concurrent;
using Wino.Core.Authentication;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Process-wide cache of ephemeral OAuth access tokens, keyed by account id.
/// Access tokens are never persisted to disk; they live here for the app session and are
/// re-derived from the durable refresh token on a miss. The interactive sign-in flow seeds
/// this cache after a fresh authorization-code exchange so the first sync needs no refresh.
/// </summary>
public sealed class ExchangeTokenCache
{
    private readonly ConcurrentDictionary<Guid, OidcTokenSet> _tokens = new();

    public bool TryGet(Guid accountId, out OidcTokenSet tokenSet) => _tokens.TryGetValue(accountId, out tokenSet);

    public void Set(Guid accountId, OidcTokenSet tokenSet) => _tokens[accountId] = tokenSet;

    public void Remove(Guid accountId) => _tokens.TryRemove(accountId, out _);
}

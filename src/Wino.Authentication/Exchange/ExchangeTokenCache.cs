#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Wino.Authentication.Exchange;

public sealed record ExchangeAccessToken(string AccessToken, DateTimeOffset ExpiresAtUtc)
{
    public bool IsValid(TimeSpan skew) => !string.IsNullOrEmpty(AccessToken) && DateTimeOffset.UtcNow + skew < ExpiresAtUtc;
}

/// <summary>In-memory Exchange access tokens per account, one per process.</summary>
public sealed class ExchangeTokenCache
{
    private readonly ConcurrentDictionary<Guid, ExchangeAccessToken> _tokens = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    public bool TryGet(Guid accountId, [NotNullWhen(true)] out ExchangeAccessToken? token) => _tokens.TryGetValue(accountId, out token);

    public void Set(Guid accountId, ExchangeAccessToken token) => _tokens[accountId] = token;

    public void Remove(Guid accountId) => _tokens.TryRemove(accountId, out _);

    /// <summary>
    /// Serializes refreshes per account. Issuers that rotate refresh tokens accept each one once, so two concurrent
    /// refreshes would make the second fail, and some issuers then revoke the whole token family.
    /// </summary>
    public SemaphoreSlim GetRefreshLock(Guid accountId) => _refreshLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
}

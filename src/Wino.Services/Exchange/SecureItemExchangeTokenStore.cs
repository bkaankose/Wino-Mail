#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Exchange;

/// <summary>
/// Exchange refresh tokens as UTF-8 secure items in the "exchange-tokens" scope, one per account. Hosts without a
/// platform secure item store get a protected-file store. The scope and key names never change once shipped.
/// </summary>
public sealed class SecureItemExchangeTokenStore(ISecureItemStore store) : IExchangeTokenStore
{
    public const string Scope = "exchange-tokens";

    public Task<string?> GetRefreshTokenAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = store.Read(Scope, Key(accountId));
        if (bytes is null) return Task.FromResult<string?>(null);
        try { return Task.FromResult<string?>(new UTF8Encoding(false, true).GetString(bytes)); }
        catch (DecoderFallbackException exception) { throw new CryptographicException("The stored Exchange token is corrupt.", exception); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task SaveRefreshTokenAsync(Guid accountId, string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(refreshToken);
        try { store.Write(Scope, Key(accountId), bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Delete(Scope, Key(accountId));
        return Task.CompletedTask;
    }

    private static string Key(Guid accountId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An Exchange token needs an account id.", nameof(accountId));
        return accountId.ToString("N");
    }
}

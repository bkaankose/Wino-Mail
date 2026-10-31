#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication.OAuth;

/// <summary>
/// Serialized OAuth tokens as secure items in the "oauth-tokens" scope (Keychain items on Apple, the Android secure
/// store on Android). Keys come from <see cref="OAuthTokenService.GetStoreKey"/>. Scope and key names never change once shipped.
/// </summary>
public sealed class SecureItemOAuthTokenStore(ISecureItemStore store) : IOAuthTokenStore
{
    public const string Scope = "oauth-tokens";

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(store.Read(Scope, key));
    }

    public Task WriteAsync(string key, byte[] serializedToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Write(Scope, key, serializedToken);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Delete(Scope, key);
        return Task.CompletedTask;
    }
}

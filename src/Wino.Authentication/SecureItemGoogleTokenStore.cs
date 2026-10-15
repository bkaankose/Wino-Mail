#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication;

/// <summary>Serialized Google tokens as secure items in the "google-tokens" scope (Keychain items on Apple).</summary>
public sealed class SecureItemGoogleTokenStore(ISecureItemStore store) : IGoogleTokenStore
{
    public const string Scope = "google-tokens";

    public Task<byte[]?> ReadAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(store.Read(Scope, credentialKey));
    }

    public Task WriteAsync(string credentialKey, byte[] serializedToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Write(Scope, credentialKey, serializedToken);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Delete(Scope, credentialKey);
        return Task.CompletedTask;
    }
}

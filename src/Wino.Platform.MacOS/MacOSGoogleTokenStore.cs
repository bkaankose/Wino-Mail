#nullable enable
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Wino.Authentication;
using Wino.Platform.MacOS.Security;

namespace Wino.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacOSGoogleTokenStore(MacOSKeychainStore keychain) : IGoogleTokenStore
{
    public Task<byte[]?> ReadAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(keychain.Read("google-tokens", credentialKey));
    }

    public Task WriteAsync(string credentialKey, byte[] serializedToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        keychain.Write("google-tokens", credentialKey, serializedToken);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        keychain.Delete("google-tokens", credentialKey);
        return Task.CompletedTask;
    }
}

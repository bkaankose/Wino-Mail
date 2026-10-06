#nullable enable
using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.MacOS.Security;

namespace Wino.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacOSDavCredentialStore(MacOSKeychainStore keychain) : IDavCredentialStore
{
    public Task<string?> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = keychain.Read("dav", accountId.ToString("N"));
        if (bytes is null) return Task.FromResult<string?>(null);
        try { return Task.FromResult<string?>(new UTF8Encoding(false, true).GetString(bytes)); }
        catch (DecoderFallbackException exception) { throw new CryptographicException("The DAV Keychain credential is corrupt.", exception); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(password ?? throw new ArgumentNullException(nameof(password)));
        try { keychain.Write("dav", accountId.ToString("N"), bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        keychain.Delete("dav", accountId.ToString("N"));
        return Task.CompletedTask;
    }
}

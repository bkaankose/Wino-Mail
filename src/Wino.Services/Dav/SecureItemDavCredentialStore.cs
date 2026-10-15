#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Dav;

/// <summary>DAV passwords as UTF-8 secure items in the "dav" scope, one per account (Keychain items on Apple).</summary>
public sealed class SecureItemDavCredentialStore(ISecureItemStore store) : IDavCredentialStore
{
    public const string Scope = "dav";

    public Task<string?> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = store.Read(Scope, accountId.ToString("N"));
        if (bytes is null) return Task.FromResult<string?>(null);
        try { return Task.FromResult<string?>(new UTF8Encoding(false, true).GetString(bytes)); }
        catch (DecoderFallbackException exception) { throw new CryptographicException("The stored DAV credential is corrupt.", exception); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(password ?? throw new ArgumentNullException(nameof(password)));
        try { store.Write(Scope, accountId.ToString("N"), bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Delete(Scope, accountId.ToString("N"));
        return Task.CompletedTask;
    }
}

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Dav;

public sealed class DavCredentialStore : IDavCredentialStore
{
    private readonly string _root;

    public DavCredentialStore(IApplicationConfiguration configuration)
    {
        _root = Path.Combine(configuration.ApplicationDataFolderPath, "credentials", "dav");
    }

    public async Task<string> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(accountId);
        if (!File.Exists(path)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1416
        var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy(accountId), DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        try { return Encoding.UTF8.GetString(clearBytes); }
        finally { CryptographicOperations.ZeroMemory(clearBytes); }
    }

    public async Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Directory.CreateDirectory(_root);
        var clearBytes = Encoding.UTF8.GetBytes(password);
        byte[] protectedBytes = null;
        try
        {
#pragma warning disable CA1416
            protectedBytes = ProtectedData.Protect(clearBytes, Entropy(accountId), DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            var destination = PathFor(accountId);
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(accountId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(Guid accountId) => Path.Combine(_root, $"{accountId:N}.bin");
    private static byte[] Entropy(Guid accountId) => SHA256.HashData(Encoding.UTF8.GetBytes($"Wino.DAV.{accountId:D}"));
}

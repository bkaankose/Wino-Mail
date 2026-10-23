using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Services.Dav;

public sealed class DavCredentialStore : IDavCredentialStore
{
    private readonly string _root;
    private readonly ISecretProtector _protector;

    public DavCredentialStore(IApplicationConfiguration configuration, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        _root = Path.Combine(configuration.ApplicationDataFolderPath, "credentials", "dav");
        _protector = protector;
    }

    public async Task<string> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(accountId);
        if (!File.Exists(path))
            return null;

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] clearBytes = null;

        try
        {
            clearBytes = Unprotect(protectedBytes, accountId, out var usedLegacyEntropy);
            var password = Encoding.UTF8.GetString(clearBytes);
            if (usedLegacyEntropy)
                await SavePasswordAsync(accountId, password, cancellationToken).ConfigureAwait(false);

            return password;
        }
        finally
        {
            if (clearBytes is not null) CryptographicOperations.ZeroMemory(clearBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private byte[] Unprotect(byte[] protectedBytes, Guid accountId, out bool usedLegacyEntropy)
    {
        var context = Entropy(accountId);
        try
        {
            usedLegacyEntropy = false;
            return _protector.Unprotect(protectedBytes, context);
        }
        catch (CryptographicException)
        {
            // Saved by a build from before the Mail-Cal rename; the caller re-protects it with the current entropy.
            var legacyContext = LegacyEntropy(accountId);
            try
            {
                usedLegacyEntropy = true;
                return _protector.Unprotect(protectedBytes, legacyContext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(legacyContext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(context);
        }
    }

    public async Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Directory.CreateDirectory(_root);
        var clearBytes = Encoding.UTF8.GetBytes(password);
        var context = Entropy(accountId);
        byte[] protectedBytes = null;
        string temporary = null;

        try
        {
            protectedBytes = _protector.Protect(clearBytes, context);
            var destination = PathFor(accountId);
            temporary = destination + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            CryptographicOperations.ZeroMemory(context);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);

            if (temporary is not null && File.Exists(temporary))
                File.Delete(temporary);
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
    private static byte[] Entropy(Guid accountId) => SHA256.HashData(Encoding.UTF8.GetBytes($"MailCal.DAV.{accountId:D}"));
    private static byte[] LegacyEntropy(Guid accountId) => SHA256.HashData(Encoding.UTF8.GetBytes($"Wino.DAV.{accountId:D}"));
}

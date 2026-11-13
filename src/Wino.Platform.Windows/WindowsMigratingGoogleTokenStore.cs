#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Platform.Windows;

/// <summary>
/// Gmail tokens in the secure item store (the <see cref="SecureItemGoogleTokenStore"/> layout) for a Windows install
/// that may still have <see cref="WindowsGoogleTokenStore"/> files. The secure item always wins; a file is read only
/// when no item exists, and it is deleted once its token is stored and read back.
/// </summary>
/// <remarks>
/// Gmail reads the token for every HTTP request, so decrypted tokens are cached per key and revalidated against the
/// item file's write time and length. Another process (the background task) that writes a token changes both.
/// </remarks>
public sealed class WindowsMigratingGoogleTokenStore(
    ISecureItemStore store,
    Func<string, string, string> itemPath,
    WindowsGoogleTokenStore legacy) : IGoogleTokenStore, ILegacyCredentialFileMigration
{
    private const string Scope = SecureItemGoogleTokenStore.Scope;

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failedThisProcess = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<WindowsMigratingGoogleTokenStore>();

    public async Task<byte[]?> ReadAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ReadSecure(credentialKey) ?? await MigrateAsync(credentialKey, lazy: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(string credentialKey, byte[] serializedToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cache.TryRemove(credentialKey, out _);
        store.Write(Scope, credentialKey, serializedToken);
        await DeleteLegacyAsync(credentialKey).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cache.TryRemove(credentialKey, out _);
        store.Delete(Scope, credentialKey);
        await legacy.DeleteAsync(credentialKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task MigrateLegacyFilesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var credentialKey in legacy.GetStoredCredentialKeys())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = await MigrateAsync(credentialKey, lazy: false, cancellationToken).ConfigureAwait(false);
            if (token is not null) CryptographicOperations.ZeroMemory(token);
        }
    }

    private byte[]? ReadSecure(string credentialKey)
    {
        var file = new FileInfo(itemPath(Scope, credentialKey));
        if (!file.Exists)
        {
            _cache.TryRemove(credentialKey, out _);
            return null;
        }

        var stamp = new FileStamp(file.LastWriteTimeUtc, file.Length);
        if (_cache.TryGetValue(credentialKey, out var cached) && cached.Stamp == stamp)
            return (byte[])cached.Token.Clone();

        var token = store.Read(Scope, credentialKey);
        if (token is null)
        {
            _cache.TryRemove(credentialKey, out _);
            return null;
        }

        _cache[credentialKey] = new CachedToken((byte[])token.Clone(), stamp);
        return token;
    }

    /// <summary>Returns the token the caller should use: the secure item's, or the legacy one when it could not move.</summary>
    private async Task<byte[]?> MigrateAsync(string credentialKey, bool lazy, CancellationToken cancellationToken)
    {
        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var legacyToken = await legacy.ReadAsync(credentialKey, cancellationToken).ConfigureAwait(false);
            if (legacyToken is null || (lazy && _failedThisProcess.ContainsKey(credentialKey)))
                return legacyToken;

            try
            {
                var result = SecureItemImport.Import(store, Scope, credentialKey, legacyToken);
                if (result == SecureItemImportResult.ReadBackFailed)
                {
                    _failedThisProcess.TryAdd(credentialKey, 0);
                    _logger.Warning("The Gmail token {CredentialKey} did not read back from the secure store; the legacy file is kept.", credentialKey);
                    return legacyToken;
                }

                await DeleteLegacyAsync(credentialKey).ConfigureAwait(false);
                if (result == SecureItemImportResult.Imported)
                    return legacyToken;

                CryptographicOperations.ZeroMemory(legacyToken);
                return ReadSecure(credentialKey);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_failedThisProcess.TryAdd(credentialKey, 0))
                    _logger.Warning(exception, "The Gmail token {CredentialKey} could not move to the secure store; the legacy file is kept.", credentialKey);
                return legacyToken;
            }
        }
        finally { _migrationLock.Release(); }
    }

    private async Task DeleteLegacyAsync(string credentialKey)
    {
        try { await legacy.DeleteAsync(credentialKey).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(exception, "The legacy Gmail token file {CredentialKey} could not be deleted.", credentialKey);
        }
    }

    private readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length);

    private sealed record CachedToken(byte[] Token, FileStamp Stamp);
}

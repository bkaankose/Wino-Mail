#nullable enable
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Services.Dav;

/// <summary>
/// DAV passwords in the secure item store (<see cref="SecureItemDavCredentialStore"/>) for a host that had
/// <see cref="DavCredentialStore"/> files. The secure item always wins; a file is read only when no item exists,
/// and it is deleted once its password is stored and read back.
/// </summary>
public sealed class LegacyMigratingDavCredentialStore(ISecureItemStore store, DavCredentialStore legacy) : IDavCredentialStore, ILegacyCredentialFileMigration
{
    private readonly SecureItemDavCredentialStore _secure = new(store);
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _failedThisProcess = new();
    private readonly ILogger _logger = Log.ForContext<LegacyMigratingDavCredentialStore>();

    public async Task<string?> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await _secure.GetPasswordAsync(accountId, cancellationToken).ConfigureAwait(false)
           ?? await MigrateAsync(accountId, lazy: true, cancellationToken).ConfigureAwait(false);

    public async Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken = default)
    {
        await _secure.SavePasswordAsync(accountId, password, cancellationToken).ConfigureAwait(false);
        await DeleteLegacyAsync(accountId).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _secure.DeleteAsync(accountId, cancellationToken).ConfigureAwait(false);
        await legacy.DeleteAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task MigrateLegacyFilesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var accountId in legacy.GetStoredAccountIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MigrateAsync(accountId, lazy: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Returns the password the caller should use: the secure item's, or the legacy one when it could not move.</summary>
    private async Task<string?> MigrateAsync(Guid accountId, bool lazy, CancellationToken cancellationToken)
    {
        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? legacyPassword;
            try { legacyPassword = await legacy.GetPasswordAsync(accountId, cancellationToken).ConfigureAwait(false); }
            catch (CryptographicException exception)
            {
                _logger.Warning(exception, "The legacy DAV password file of account {AccountId} cannot be decrypted; it is kept.", accountId);
                return null;
            }

            if (legacyPassword is null || (lazy && _failedThisProcess.ContainsKey(accountId)))
                return legacyPassword;

            var bytes = Encoding.UTF8.GetBytes(legacyPassword);
            try
            {
                var result = SecureItemImport.Import(store, SecureItemDavCredentialStore.Scope, accountId.ToString("N"), bytes);
                if (result == SecureItemImportResult.ReadBackFailed)
                {
                    _failedThisProcess.TryAdd(accountId, 0);
                    _logger.Warning("The DAV password of account {AccountId} did not read back from the secure store; the legacy file is kept.", accountId);
                    return legacyPassword;
                }

                await DeleteLegacyAsync(accountId).ConfigureAwait(false);
                return result == SecureItemImportResult.Imported
                    ? legacyPassword
                    : await _secure.GetPasswordAsync(accountId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_failedThisProcess.TryAdd(accountId, 0))
                    _logger.Warning(exception, "The DAV password of account {AccountId} could not move to the secure store; the legacy file is kept.", accountId);
                return legacyPassword;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { _migrationLock.Release(); }
    }

    private async Task DeleteLegacyAsync(Guid accountId)
    {
        try { await legacy.DeleteAsync(accountId).ConfigureAwait(false); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.Warning(exception, "The legacy DAV password file of account {AccountId} could not be deleted.", accountId);
        }
    }
}

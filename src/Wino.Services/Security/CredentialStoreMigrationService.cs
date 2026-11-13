#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Security;

/// <summary>
/// Moves plaintext credential columns into secure item revisions and imports legacy credential files.
/// A row changes only after its revision was written and read back; the update is conditional on the plaintext
/// still being there, so a concurrent credential change wins. A failed write (the protector or the disk is
/// unavailable) keeps the plaintext for the next launch. Only a read-back failure at startup followed by another
/// one in the lazy retry of the same process clears the plaintext and asks the user to sign in again.
/// </summary>
public sealed class CredentialStoreMigrationService(
    IDatabaseService databaseService,
    SecureItemAccountCredentialPersistence persistence,
    Func<IAccountService> accountService,
    Func<IWinoAccountSessionService> sessions,
    IEnumerable<ILegacyCredentialFileMigration> legacyFiles) : ICredentialStoreMigrationService
{
    private static readonly string LegacyServerCondition =
        $"{LegacyColumn(nameof(CustomServerInformation.IncomingServerPassword))} OR " +
        $"{LegacyColumn(nameof(CustomServerInformation.OutgoingServerPassword))} OR " +
        $"{LegacyColumn(nameof(CustomServerInformation.CalDavPassword))}";
    private static readonly string LegacyWinoCondition =
        $"{LegacyColumn(nameof(WinoAccount.AccessToken))} OR {LegacyColumn(nameof(WinoAccount.RefreshToken))}";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(StoredCredentialKind Kind, Guid AccountId), byte> _lazyAttempts = new();
    private readonly ConcurrentDictionary<(StoredCredentialKind Kind, Guid AccountId), byte> _startupVerificationFailures = new();
    private readonly ILogger _logger = Log.ForContext<CredentialStoreMigrationService>();
    private int _writeFailureLogged;

    private enum MigrationOutcome { NotLegacy, Migrated, WriteFailed, VerificationFailed, DatabaseFailed }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try { await MigrateDatabaseAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { _logger.Warning(exception, "Plaintext credentials could not be moved to the secure store; they are retried on the next launch."); }

            foreach (var source in legacyFiles)
            {
                try { await source.MigrateLegacyFilesAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.Warning(exception, "Legacy credential files could not be moved to the secure store; they are retried on the next launch."); }
            }
        }
        finally { _gate.Release(); }
    }

    public void ReportLegacyPlaintext(StoredCredentialKind kind, Guid accountId)
    {
        if (accountId == Guid.Empty || !_lazyAttempts.TryAdd((kind, accountId), 0))
            return;

        // Reads report from inside session and account locks, so the retry and any re-authentication run apart from them.
        _ = Task.Run(() => RetryAsync(kind, accountId));
    }

    private async Task MigrateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = databaseService.Connection;
        var pending = await connection.ExecuteScalarAsync<int>(
            $"SELECT (SELECT COUNT(*) FROM {nameof(CustomServerInformation)} WHERE {LegacyServerCondition}) + " +
            $"(SELECT COUNT(*) FROM {nameof(WinoAccount)} WHERE {LegacyWinoCondition})").ConfigureAwait(false);
        if (pending == 0) return;

        foreach (var information in await connection.Table<CustomServerInformation>().ToListAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await MigrateServerInformationAsync(information).ConfigureAwait(false);
            if (outcome == MigrationOutcome.VerificationFailed)
                _startupVerificationFailures.TryAdd((StoredCredentialKind.MailServerPasswords, information.AccountId), 0);
        }

        foreach (var account in await connection.Table<WinoAccount>().ToListAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await MigrateWinoAccountAsync(account).ConfigureAwait(false);
            if (outcome == MigrationOutcome.VerificationFailed)
                _startupVerificationFailures.TryAdd((StoredCredentialKind.WinoAccountTokens, account.Id), 0);
        }
    }

    private async Task RetryAsync(StoredCredentialKind kind, Guid accountId)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            MigrationOutcome outcome;
            try
            {
                var connection = databaseService.Connection;
                if (kind == StoredCredentialKind.MailServerPasswords)
                {
                    var information = await connection.Table<CustomServerInformation>()
                        .FirstOrDefaultAsync(candidate => candidate.AccountId == accountId).ConfigureAwait(false);
                    outcome = information is null ? MigrationOutcome.NotLegacy : await MigrateServerInformationAsync(information).ConfigureAwait(false);
                }
                else
                {
                    var account = await connection.Table<WinoAccount>()
                        .FirstOrDefaultAsync(candidate => candidate.Id == accountId).ConfigureAwait(false);
                    outcome = account is null ? MigrationOutcome.NotLegacy : await MigrateWinoAccountAsync(account).ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }

            if (outcome == MigrationOutcome.VerificationFailed && _startupVerificationFailures.ContainsKey((kind, accountId)))
                await ClearPlaintextAsync(kind, accountId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "The credential migration retry for account {AccountId} failed; it runs again on the next launch.", accountId);
        }
    }

    private async Task<MigrationOutcome> MigrateServerInformationAsync(CustomServerInformation information)
    {
        string?[] secrets = [information.IncomingServerPassword, information.OutgoingServerPassword, information.CalDavPassword];
        return await MigrateAsync(StoredCredentialKind.MailServerPasswords, information.AccountId, secrets, reference =>
            databaseService.Connection.ExecuteAsync(
                $"UPDATE {nameof(CustomServerInformation)} SET {nameof(CustomServerInformation.IncomingServerPassword)} = ?, " +
                $"{nameof(CustomServerInformation.OutgoingServerPassword)} = ?, {nameof(CustomServerInformation.CalDavPassword)} = ? " +
                $"WHERE {nameof(CustomServerInformation.Id)} = ? AND {nameof(CustomServerInformation.IncomingServerPassword)} IS ? AND " +
                $"{nameof(CustomServerInformation.OutgoingServerPassword)} IS ? AND {nameof(CustomServerInformation.CalDavPassword)} IS ?",
                reference, reference, reference, information.Id, secrets[0], secrets[1], secrets[2])).ConfigureAwait(false);
    }

    private async Task<MigrationOutcome> MigrateWinoAccountAsync(WinoAccount account)
    {
        string?[] secrets = [account.AccessToken, account.RefreshToken];
        return await MigrateAsync(StoredCredentialKind.WinoAccountTokens, account.Id, secrets, reference =>
            databaseService.Connection.ExecuteAsync(
                $"UPDATE {nameof(WinoAccount)} SET {nameof(WinoAccount.AccessToken)} = ?, {nameof(WinoAccount.RefreshToken)} = ? " +
                $"WHERE {nameof(WinoAccount.Id)} = ? AND {nameof(WinoAccount.AccessToken)} IS ? AND {nameof(WinoAccount.RefreshToken)} IS ?",
                reference, reference, account.Id, secrets[0], secrets[1])).ConfigureAwait(false);
    }

    private async Task<MigrationOutcome> MigrateAsync(StoredCredentialKind kind, Guid accountId, string?[] secrets, Func<string, Task<int>> updateDatabase)
    {
        if (!SecureItemAccountCredentialPersistence.IsLegacyPlaintext(secrets))
            return MigrationOutcome.NotLegacy;

        string reference;
        try { reference = persistence.WriteMigrationRevision(kind, accountId, secrets); }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _writeFailureLogged, 1) == 0)
                _logger.Warning(exception, "The secure credential store cannot be written; plaintext credentials are kept until a later launch.");
            return MigrationOutcome.WriteFailed;
        }

        if (!persistence.VerifyRevision(kind, accountId, reference, secrets))
        {
            persistence.DeleteMigrationRevision(kind, accountId, reference);
            _logger.Warning("The migrated {Kind} credential of account {AccountId} did not read back; the plaintext is kept for now.", kind, accountId);
            return MigrationOutcome.VerificationFailed;
        }

        int updated;
        try { updated = await updateDatabase(reference).ConfigureAwait(false); }
        catch (Exception exception)
        {
            _logger.Warning(exception, "The database could not store the migrated {Kind} credential of account {AccountId}.", kind, accountId);
            updated = 0;
        }

        if (updated != 1)
        {
            persistence.DeleteMigrationRevision(kind, accountId, reference);
            return MigrationOutcome.DatabaseFailed;
        }

        _logger.Information("Moved the {Kind} credential of account {AccountId} to the secure store.", kind, accountId);
        return MigrationOutcome.Migrated;
    }

    private async Task ClearPlaintextAsync(StoredCredentialKind kind, Guid accountId)
    {
        _logger.Warning("The {Kind} credential of account {AccountId} failed secure storage twice; it is removed and sign-in is required.", kind, accountId);

        if (kind == StoredCredentialKind.MailServerPasswords)
        {
            var information = await databaseService.Connection.Table<CustomServerInformation>()
                .FirstOrDefaultAsync(candidate => candidate.AccountId == accountId).ConfigureAwait(false);
            if (information is not null && SecureItemAccountCredentialPersistence.IsLegacyPlaintext(
                    [information.IncomingServerPassword, information.OutgoingServerPassword, information.CalDavPassword]))
                await accountService().DeleteAccountAuthenticationDataAsync(accountId).ConfigureAwait(false);
            return;
        }

        var account = await databaseService.Connection.Table<WinoAccount>()
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId).ConfigureAwait(false);
        if (account is null || !SecureItemAccountCredentialPersistence.IsLegacyPlaintext([account.AccessToken, account.RefreshToken]))
            return;

        await databaseService.Connection.ExecuteAsync(
            $"UPDATE {nameof(WinoAccount)} SET {nameof(WinoAccount.AccessToken)} = '', {nameof(WinoAccount.RefreshToken)} = '' " +
            $"WHERE {nameof(WinoAccount.Id)} = ? AND {nameof(WinoAccount.AccessToken)} IS ? AND {nameof(WinoAccount.RefreshToken)} IS ?",
            accountId, account.AccessToken, account.RefreshToken).ConfigureAwait(false);

        var sessionService = sessions();
        var session = await sessionService.CaptureAsync().ConfigureAwait(false);
        if (session is not null && session.AccountId == accountId)
            await sessionService.MarkInvalidAsync(session, "secure credential storage failed").ConfigureAwait(false);
    }

    private static string LegacyColumn(string column)
        => $"({column} IS NOT NULL AND {column} <> '' AND substr({column}, 1, {SecureItemAccountCredentialPersistence.Prefix.Length}) <> '{SecureItemAccountCredentialPersistence.Prefix}')";
}

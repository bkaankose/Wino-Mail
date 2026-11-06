#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class WinoAccountSessionService(IDatabaseService databaseService, IAccountCredentialPersistence credentialPersistence) : IWinoAccountSessionService
{
    private static readonly ConditionalWeakTable<IDatabaseService, WinoAccountSessionService> Shared = new();
    private static readonly object SharedLock = new();
    private readonly IAccountCredentialPersistence _credentialPersistence = credentialPersistence;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource _sessionCancellation = new();
    private long _generation;

    // Keeps manually constructed clients and profile services on the same coordinator too.
    internal static IWinoAccountSessionService For(IDatabaseService databaseService, IAccountCredentialPersistence credentialPersistence)
    {
        lock (SharedLock)
        {
            var service = Shared.GetValue(databaseService, database => new(database, credentialPersistence));
            if (!ReferenceEquals(service._credentialPersistence, credentialPersistence))
                throw new InvalidOperationException("A database cannot use multiple credential persistence policies.");
            return service;
        }
    }

    public async Task<WinoAccountSession?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = await ReadMetadataAsync().ConfigureAwait(false);
            return account is null ? null : new(account.Id, _generation, _sessionCancellation.Token);
        }
        finally { _stateLock.Release(); }
    }

    public Task<bool> IsCurrentAsync(WinoAccountSession session, CancellationToken cancellationToken = default)
        => CommitAsync(session, () => Task.CompletedTask, cancellationToken);

    public async Task<bool> CommitAsync(WinoAccountSession session, Func<Task> commit, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Generation != _generation || session.CancellationToken.IsCancellationRequested ||
                (await ReadMetadataAsync().ConfigureAwait(false))?.Id != session.AccountId)
                return false;

            await commit().ConfigureAwait(false);
            return true;
        }
        finally { _stateLock.Release(); }
    }

    public async Task ReplaceAsync(WinoAccount? account, Func<Task> beforeReplace, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
            var stored = account is null ? null : await _credentialPersistence.PrepareWinoAccountForStorageAsync(account, cancellationToken).ConfigureAwait(false);
            _generation++;
            _sessionCancellation.Cancel();
            _sessionCancellation = new();

            await beforeReplace().ConfigureAwait(false);
            await databaseService.Connection.RunInTransactionAsync(connection =>
            {
                connection.DeleteAll<WinoAccount>();
                if (stored is not null) connection.Insert(stored, typeof(WinoAccount));
            }).ConfigureAwait(false);
            if (previous is not null && previous.Id != account?.Id)
                await _credentialPersistence.DeleteWinoAccountSecretsAsync(previous.Id, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _stateLock.Release(); }
    }

    public async Task<WinoAccount?> RefreshCredentialsAsync(WinoAccountSession session, string? rejectedAccessToken,
        Func<WinoAccount, CancellationToken, Task<WinoAccount?>> refresh, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        await _refreshLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            WinoAccount? account = null;
            if (!await CommitAsync(session, async () => account = await ReadAsync().ConfigureAwait(false), linked.Token).ConfigureAwait(false))
                return null;

            if (account is null || string.IsNullOrWhiteSpace(account.RefreshToken)) return null;
            if (!string.IsNullOrWhiteSpace(account.AccessToken) &&
                (rejectedAccessToken is null ? account.AccessTokenExpiresAtUtc > DateTime.UtcNow : account.AccessToken != rejectedAccessToken))
                return account;

            var refreshed = await refresh(account, linked.Token).ConfigureAwait(false);
            if (refreshed is null || refreshed.Id != session.AccountId) return null;

            WinoAccount? committed = null;
            var saved = await CommitAsync(session, async () =>
            {
                // Profile requests may have committed newer account data while credentials rotated.
                committed = (await ReadAsync().ConfigureAwait(false))!;
                committed.AccessToken = refreshed.AccessToken;
                committed.AccessTokenExpiresAtUtc = refreshed.AccessTokenExpiresAtUtc;
                committed.RefreshToken = refreshed.RefreshToken;
                committed.RefreshTokenExpiresAtUtc = refreshed.RefreshTokenExpiresAtUtc;
                var stored = await _credentialPersistence.PrepareWinoAccountForStorageAsync(committed, linked.Token).ConfigureAwait(false);
                await databaseService.Connection.UpdateAsync(stored, typeof(WinoAccount)).ConfigureAwait(false);
            }, linked.Token).ConfigureAwait(false);

            return saved ? committed : null;
        }
        finally { _refreshLock.Release(); }
    }

    private async Task<WinoAccount?> ReadAsync()
    {
        var account = await ReadMetadataAsync().ConfigureAwait(false);
        if (account is not null)
            await _credentialPersistence.RestoreWinoAccountSecretsAsync(account).ConfigureAwait(false);
        return account;
    }

    private async Task<WinoAccount?> ReadMetadataAsync()
        => await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
}

using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class Pop3PersistenceService : BaseDatabaseService, IPop3PersistenceService
{
    public Pop3PersistenceService(IDatabaseService databaseService, IMessenger messenger) : base(databaseService, messenger)
    {
    }

    public async Task<IReadOnlyDictionary<string, Pop3RemoteMessageState>> GetRemoteStatesAsync(Guid accountId)
    {
        var states = (await Connection.Table<Pop3RemoteMessageState>()
                .Where(item => item.AccountId == accountId)
                .ToListAsync()
                .ConfigureAwait(false))
            .GroupBy(item => item.Uidl, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var importedUidls = await Connection.QueryScalarsAsync<string>(
            "SELECT Pop3Uidl FROM MailCopy WHERE FolderId IN " +
            "(SELECT Id FROM MailItemFolder WHERE MailAccountId = ?) " +
            "AND Pop3Uidl IS NOT NULL AND Pop3Uidl <> ''",
            accountId).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        foreach (var uidl in importedUidls.Distinct(StringComparer.Ordinal))
        {
            if (states.ContainsKey(uidl))
                continue;

            var state = new Pop3RemoteMessageState
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Uidl = uidl,
                EvaluatedAtUtc = now,
                IsImported = true
            };
            await Connection.InsertAsync(state, typeof(Pop3RemoteMessageState)).ConfigureAwait(false);
            states[uidl] = state;
        }

        return states;
    }

    public async Task MarkUidlKnownAsync(Guid accountId, string uidl, bool isImported)
    {
        if (string.IsNullOrWhiteSpace(uidl))
            throw new ArgumentException("A POP3 UIDL is required.", nameof(uidl));

        var existing = await Connection.Table<Pop3RemoteMessageState>()
            .FirstOrDefaultAsync(item => item.AccountId == accountId && item.Uidl == uidl)
            .ConfigureAwait(false);

        if (existing != null)
        {
            if (isImported && !existing.IsImported)
            {
                existing.IsImported = true;
                existing.EvaluatedAtUtc = DateTime.UtcNow;
                await Connection.UpdateAsync(existing, typeof(Pop3RemoteMessageState)).ConfigureAwait(false);
            }

            return;
        }

        await Connection.InsertAsync(new Pop3RemoteMessageState
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Uidl = uidl,
            EvaluatedAtUtc = DateTime.UtcNow,
            IsImported = isImported
        }, typeof(Pop3RemoteMessageState)).ConfigureAwait(false);
    }

    public async Task PruneRemoteStatesAsync(Guid accountId, IEnumerable<string> serverUidls)
    {
        var onServer = serverUidls?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var states = await Connection.Table<Pop3RemoteMessageState>()
            .Where(item => item.AccountId == accountId)
            .ToListAsync()
            .ConfigureAwait(false);

        var gone = states.Where(item => !onServer.Contains(item.Uidl)).ToList();
        if (gone.Count == 0)
            return;

        // A local copy keeps its row; GetRemoteStatesAsync would only add it back.
        var referenced = (await Connection.QueryScalarsAsync<string>(
                "SELECT Pop3Uidl FROM MailCopy WHERE FolderId IN " +
                "(SELECT Id FROM MailItemFolder WHERE MailAccountId = ?) " +
                "AND Pop3Uidl IS NOT NULL AND Pop3Uidl <> ''",
                accountId).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var state in gone.Where(item => !referenced.Contains(item.Uidl)))
            await Connection.DeleteAsync<Pop3RemoteMessageState>(state.Id).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Pop3PendingServerDeletion>> GetPendingDeletionsAsync(Guid accountId)
        => await Connection.Table<Pop3PendingServerDeletion>()
            .Where(item => item.AccountId == accountId)
            .ToListAsync()
            .ConfigureAwait(false);

    public async Task AddPendingDeletionAsync(Guid accountId, string uidl)
    {
        if (string.IsNullOrWhiteSpace(uidl))
            throw new ArgumentException("A POP3 UIDL is required.", nameof(uidl));

        var existing = await Connection.Table<Pop3PendingServerDeletion>()
            .FirstOrDefaultAsync(item => item.AccountId == accountId && item.Uidl == uidl)
            .ConfigureAwait(false);

        if (existing != null)
            return;

        await Connection.InsertAsync(new Pop3PendingServerDeletion
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Uidl = uidl,
            CreatedAtUtc = DateTime.UtcNow
        }, typeof(Pop3PendingServerDeletion)).ConfigureAwait(false);
    }

    public async Task MarkDeletionAttemptFailedAsync(Guid tombstoneId, string error)
    {
        var item = await Connection.FindAsync<Pop3PendingServerDeletion>(tombstoneId).ConfigureAwait(false);
        if (item == null)
            return;

        item.AttemptCount++;
        item.LastAttemptUtc = DateTime.UtcNow;
        item.LastError = error ?? string.Empty;
        await Connection.UpdateAsync(item, typeof(Pop3PendingServerDeletion)).ConfigureAwait(false);
    }

    public async Task RemovePendingDeletionsAsync(IEnumerable<Guid> tombstoneIds)
    {
        foreach (var id in tombstoneIds?.Distinct() ?? [])
            await Connection.DeleteAsync<Pop3PendingServerDeletion>(id).ConfigureAwait(false);
    }

    public async Task DeleteAccountStateAsync(Guid accountId)
    {
        await Connection.Table<Pop3PendingServerDeletion>().DeleteAsync(item => item.AccountId == accountId).ConfigureAwait(false);
        await Connection.Table<Pop3RemoteMessageState>().DeleteAsync(item => item.AccountId == accountId).ConfigureAwait(false);
    }
}

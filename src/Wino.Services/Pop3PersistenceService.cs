using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class Pop3PersistenceService : BaseDatabaseService, IPop3PersistenceService
{
    public Pop3PersistenceService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public async Task<HashSet<string>> GetKnownUidlsAsync(Guid accountId)
    {
        var values = await Connection.QueryScalarsAsync<string>(
            "SELECT Pop3Uidl FROM MailCopy WHERE FolderId IN " +
            "(SELECT Id FROM MailItemFolder WHERE MailAccountId = ?) " +
            "AND Pop3Uidl IS NOT NULL AND Pop3Uidl <> ''",
            accountId).ConfigureAwait(false);

        var known = values.ToHashSet(StringComparer.Ordinal);
        var evaluated = await Connection.Table<Pop3RemoteMessageState>()
            .Where(item => item.AccountId == accountId)
            .ToListAsync()
            .ConfigureAwait(false);
        known.UnionWith(evaluated.Select(item => item.Uidl));
        return known;
    }

    public async Task MarkUidlKnownAsync(Guid accountId, string uidl)
    {
        if (string.IsNullOrWhiteSpace(uidl))
            throw new ArgumentException("A POP3 UIDL is required.", nameof(uidl));

        var existing = await Connection.Table<Pop3RemoteMessageState>()
            .FirstOrDefaultAsync(item => item.AccountId == accountId && item.Uidl == uidl)
            .ConfigureAwait(false);
        if (existing != null)
            return;

        await Connection.InsertAsync(new Pop3RemoteMessageState
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Uidl = uidl,
            EvaluatedAtUtc = DateTime.UtcNow
        }, typeof(Pop3RemoteMessageState)).ConfigureAwait(false);
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

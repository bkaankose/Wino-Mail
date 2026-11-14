using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public interface IPop3PersistenceService
{
    /// <summary>
    /// Every UIDL this account has evaluated, keyed by UIDL. Messages imported before the state was
    /// recorded for imports are added on the first call, dated now.
    /// </summary>
    Task<IReadOnlyDictionary<string, Pop3RemoteMessageState>> GetRemoteStatesAsync(Guid accountId);
    Task MarkUidlKnownAsync(Guid accountId, string uidl, bool isImported);
    /// <summary>Forgets UIDLs that are gone from the server and have no local copy.</summary>
    Task PruneRemoteStatesAsync(Guid accountId, IEnumerable<string> serverUidls);
    Task<IReadOnlyList<Pop3PendingServerDeletion>> GetPendingDeletionsAsync(Guid accountId);
    Task AddPendingDeletionAsync(Guid accountId, string uidl);
    Task MarkDeletionAttemptFailedAsync(Guid tombstoneId, string error);
    Task RemovePendingDeletionsAsync(IEnumerable<Guid> tombstoneIds);
    Task DeleteAccountStateAsync(Guid accountId);
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public interface IPop3PersistenceService
{
    Task<HashSet<string>> GetKnownUidlsAsync(Guid accountId);
    Task MarkUidlKnownAsync(Guid accountId, string uidl);
    Task<IReadOnlyList<Pop3PendingServerDeletion>> GetPendingDeletionsAsync(Guid accountId);
    Task AddPendingDeletionAsync(Guid accountId, string uidl);
    Task MarkDeletionAttemptFailedAsync(Guid tombstoneId, string error);
    Task RemovePendingDeletionsAsync(IEnumerable<Guid> tombstoneIds);
    Task DeleteAccountStateAsync(Guid accountId);
}

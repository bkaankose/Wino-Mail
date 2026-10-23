using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Models.Synchronization;
using System.Collections.Generic;

namespace MailCal.Core.Domain.Interfaces;

public interface ICardDavSynchronizationEngine
{
    Task ExecuteRequestsAsync(
        MailAccount account,
        IReadOnlyList<IContactActionRequest> requests,
        CancellationToken cancellationToken = default);

    Task<ContactSynchronizationResult> SynchronizeAsync(
        MailAccount account,
        ContactSynchronizationOptions options,
        CancellationToken cancellationToken = default);
}

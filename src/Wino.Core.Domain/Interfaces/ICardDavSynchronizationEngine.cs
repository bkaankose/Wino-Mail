using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Synchronization;
using System.Collections.Generic;

namespace Wino.Core.Domain.Interfaces;

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

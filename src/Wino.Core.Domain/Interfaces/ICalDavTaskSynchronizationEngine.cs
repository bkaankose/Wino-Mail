using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Synchronizes the VTODO collections of an IMAP account's CalDAV server with the account's
/// task lists, and writes task mutations back as VTODO resources.
/// </summary>
public interface ICalDavTaskSynchronizationEngine
{
    Task ExecuteRequestsAsync(
        MailAccount account,
        IReadOnlyList<ITaskActionRequest> requests,
        CancellationToken cancellationToken = default);

    Task<TaskSynchronizationResult> SynchronizeAsync(
        MailAccount account,
        TaskSynchronizationOptions options,
        CancellationToken cancellationToken = default);
}

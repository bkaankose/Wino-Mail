using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

public interface IBaseSynchronizer
{
    /// <summary>
    /// Account that is assigned for this synchronizer.
    /// </summary>
    MailAccount Account { get; }

    /// <summary>
    /// Synchronizer state.
    /// </summary>
    AccountSynchronizerState State { get; }

    /// <summary>
    /// Queues a single request to be executed in the next synchronization.
    /// </summary>
    /// <param name="request">Request to queue.</param>
    void QueueRequest(IRequestBase request);

    /// <summary>
    /// Returns whether there is an in-progress (queued or currently executing) operation for the given mail unique id.
    /// </summary>
    /// <param name="mailUniqueId">Mail unique id to check.</param>
    bool HasPendingOperation(Guid mailUniqueId);

    /// <summary>
    /// Returns mail unique ids that currently have queued or executing operations.
    /// </summary>
    IReadOnlyCollection<Guid> GetPendingOperationUniqueIds();

    /// <summary>
    /// Returns whether there is an in-progress (queued or currently executing) operation for the given calendar item id.
    /// </summary>
    /// <param name="calendarItemId">Calendar item id to check.</param>
    bool HasPendingCalendarOperation(Guid calendarItemId);

    /// <summary>
    /// Returns calendar item ids that currently have queued or executing operations.
    /// </summary>
    IReadOnlyCollection<Guid> GetPendingCalendarOperationIds();

    /// <summary>
    /// Synchronizes profile information with the server.
    /// Sender name and Profile picture are updated.
    /// </summary>
    /// <returns>Profile information model that holds the values.</returns>
    Task<ProfileInformation> GetProfileInformationAsync();
}

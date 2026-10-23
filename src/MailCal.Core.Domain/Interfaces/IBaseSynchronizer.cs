using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Accounts;

namespace MailCal.Core.Domain.Interfaces;

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
    /// Returns whether the synchronizer currently has requests waiting to execute.
    /// </summary>
    bool HasQueuedRequests();

    /// <summary>
    /// Returns a copy of the requests waiting to execute, in queue order. The queue is unchanged.
    /// </summary>
    IReadOnlyList<IRequestBase> GetQueuedRequestSnapshot();

    /// <summary>
    /// Removes and returns every request waiting to execute, in queue order, so a replacement
    /// synchronizer for the same account can queue them again instead of losing the user's actions.
    /// </summary>
    IReadOnlyList<IRequestBase> TakeQueuedRequests();

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

    bool HasPendingContactOperation(Guid contactId);

    IReadOnlyCollection<Guid> GetPendingContactOperationIds();

    /// <summary>
    /// Synchronizes profile information with the server.
    /// Sender name and Profile picture are updated.
    /// </summary>
    /// <returns>Profile information model that holds the values.</returns>
    Task<ProfileInformation> GetProfileInformationAsync();
}

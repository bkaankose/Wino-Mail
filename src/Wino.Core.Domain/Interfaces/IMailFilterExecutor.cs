using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public interface IMailFilterExecutor
{
    /// <summary>
    /// Determines whether an incrementally retrieved message has a Wino-managed action
    /// that will be queued after the current synchronization.
    /// </summary>
    Task<bool> ShouldSuppressNewMessageAsync(
        Guid accountId,
        string sourceRemoteFolderId,
        MailCopy message,
        CancellationToken cancellationToken = default);

    /// <summary>
     /// Evaluates newly inserted provider message IDs and queues Wino-managed actions.
    /// Returns provider message IDs whose successful planned actions should suppress notifications.
    /// </summary>
    Task<IReadOnlySet<string>> ProcessNewMessagesAsync(
        Guid accountId,
        IEnumerable<string> remoteMessageIds,
        CancellationToken cancellationToken = default);
}

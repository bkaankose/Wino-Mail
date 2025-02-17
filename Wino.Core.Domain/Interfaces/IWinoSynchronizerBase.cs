using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoSynchronizerBase : IBaseSynchronizer
{
    /// <summary>
    /// Performs a full synchronization with the server with given options.
    /// This will also prepares batch requests for execution.
    /// Requests are executed in the order they are queued and happens before the synchronization.
    /// Result of the execution queue is processed during the synchronization.
    /// </summary>
    /// <param name="options">Options for synchronization.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result summary of synchronization.</returns>
    Task<MailSynchronizationResult> SynchronizeMailsAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default);

    Task<CalendarSynchronizationResult> SynchronizeCalendarEventsAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a single MIME message from the server and saves it to disk.
    /// </summary>
    /// <param name="mailItem">Mail item to download from server.</param>
    /// <param name="transferProgress">Optional progress reporting for download operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DownloadMissingMimeMessageAsync(IMailItem mailItem, ITransferProgress transferProgress, CancellationToken cancellationToken = default);

    /// <summary>
    /// 1. Cancel active synchronization.
    /// 2. Stop all running tasks.
    /// 3. Dispose all resources.
    /// </summary>
    Task KillSynchronizerAsync();

    /// <summary>
    /// Perform online search on the server.
    /// </summary>
    /// <param name="queryText">Search query.</param>
    /// <param name="folders">Folders to include in search. All folders if null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results after downloading missing mail copies from server.</returns>
    Task<List<MailCopy>> OnlineSearchAsync(string queryText, List<IMailItemFolder> folders, CancellationToken cancellationToken = default);
}

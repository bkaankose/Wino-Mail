using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Models.MailItem;

namespace Wino.Services.Interfaces;

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

    Task<ContactSynchronizationResult> SynchronizeContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default);

    Task<TaskSynchronizationResult> SynchronizeTasksAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default);

    Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot, MailCopy draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a single MIME message from the server and saves it to disk.
    /// </summary>
    /// <param name="mailItem">Mail item to download from server.</param>
    /// <param name="transferProgress">Optional progress reporting for download operation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DownloadMissingMimeMessageAsync(MailCopy mailItem, ITransferProgress transferProgress, CancellationToken cancellationToken = default);

    /// <summary>Whether this account can store an exact copy of a message moved from another account.</summary>
    bool SupportsMailImport { get; }

    /// <summary>
    /// Stores the message in the target folder on the server, outside the request queue. The database is not written:
    /// the next synchronization of the folder brings the copy.
    /// </summary>
    Task<MailImportResult> ImportMailAsync(MailImportPackage package, CancellationToken cancellationToken = default);

    /// <summary>
    /// 1. Cancel active synchronization.
    /// 2. Stop all running tasks.
    /// 3. Dispose all resources.
    /// </summary>
    Task KillSynchronizerAsync();

    /// <summary>
    /// Perform online search on the server.
    /// </summary>
    /// <param name="criteria">Structured provider-neutral search criteria.</param>
    /// <param name="folders">Folders to include in search. All folders if null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results after downloading missing mail copies from server.</returns>
    Task<List<MailCopy>> OnlineSearchAsync(RemoteMailSearchCriteria criteria, List<IMailItemFolder> folders, CancellationToken cancellationToken = default);
    Task DownloadCalendarAttachmentAsync(CalendarItem calendarItem, CalendarAttachment attachment, string localFilePath, CancellationToken cancellationToken);
}

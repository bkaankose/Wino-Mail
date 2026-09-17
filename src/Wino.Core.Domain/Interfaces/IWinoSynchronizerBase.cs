using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Rules;
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

    // Direct request/response surfaces (not queued mutations): the Exchange provider overrides these,
    // everything else keeps the defaults. Defaults live here so fakes and the local synchronizers
    // stay untouched.

    /// <summary>Whether this provider exposes server-side inbox rules (currently Exchange only).</summary>
    bool SupportsInboxRules => false;

    /// <summary>Reads the account's server-side inbox rules. Throws <see cref="System.NotSupportedException"/> when unsupported.</summary>
    Task<IReadOnlyList<RemoteInboxRule>> GetInboxRulesAsync(CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>
    /// Applies a batch of create/update/delete changes to the account's server-side inbox rules.
    /// <paramref name="removeOutlookRuleBlob"/> clears classic Outlook's legacy rule blob when the server
    /// reports it blocks updates; only pass true after user consent (Outlook client-only rules are lost).
    /// </summary>
    Task<InboxRuleUpdateResult> UpdateInboxRulesAsync(IReadOnlyList<InboxRuleChange> changes, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>Whether this provider keeps a server-side Safe/Blocked senders list that <see cref="UpdateServerJunkListAsync"/> can edit.</summary>
    bool SupportsServerJunkLists => false;

    /// <summary>Adds or removes <paramref name="address"/> on the provider's Safe or Blocked senders list.</summary>
    Task UpdateServerJunkListAsync(string address, JunkListType listType, bool add, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>Whether this provider exposes a Global Address List (directory) for recipient resolution (currently Exchange only).</summary>
    bool SupportsGlobalAddressList => false;

    /// <summary>
    /// Searches the provider's Global Address List (directory) for <paramref name="query"/> and returns up to
    /// <paramref name="maxResults"/> matches as transient <see cref="AccountContact"/> entries that are never
    /// persisted; they feed recipient autocomplete. Mail-enabled distribution lists ride along as addressable entries.
    /// </summary>
    Task<IReadOnlyList<AccountContact>> SearchGlobalAddressListAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();
}

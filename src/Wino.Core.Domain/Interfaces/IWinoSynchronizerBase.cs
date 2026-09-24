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
using Wino.Core.Domain.Models.PublicFolders;
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
    /// Whether this provider keeps a list of the addresses the mailbox has written to. False unless a
    /// provider says otherwise, so the two methods below need no implementation to opt out.
    /// </summary>
    bool RemembersRecipients { get; }

    /// <summary>The remembered addresses, heaviest first. Empty when the provider keeps none.</summary>
    Task<IReadOnlyList<RememberedRecipient>> GetRememberedRecipientsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds these addresses to the remembered list and saves it. The provider keeps whatever it read
    /// and applies the additions to that, so per-entry data this code does not interpret - another
    /// client's, usually - is never dropped on the way back.
    /// </summary>
    Task RememberRecipientsAsync(IReadOnlyList<RememberedRecipient> recipients, CancellationToken cancellationToken = default);

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

    /// <summary>Whether this provider exposes the organization's public folders for read-only browsing (currently Exchange only).</summary>
    bool SupportsPublicFolders => false;

    /// <summary>Direct children of a public folder, or of the public folders root when <paramref name="parentFolderId"/> is null. Live, never persisted.</summary>
    Task<IReadOnlyList<PublicFolderNode>> GetPublicFolderChildrenAsync(string parentFolderId, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>A page of a public mail folder's items as transient copies (FolderId is empty, AssignedAccount is set). A non-positive <paramref name="take"/> means the provider's default page.</summary>
    Task<IReadOnlyList<MailCopy>> GetPublicFolderMailItemsAsync(string folderId, int skip, int take, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>Raw MIME of a single public folder message.</summary>
    Task<byte[]> GetPublicFolderMailMimeAsync(string folderId, string itemId, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>Appointments of a public calendar folder inside a UTC window, expanded into occurrences, as transient locked calendar items.</summary>
    Task<IReadOnlyList<CalendarItem>> GetPublicFolderAppointmentsAsync(string folderId, System.DateTime startUtc, System.DateTime endUtc, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>Contacts of a public contacts folder as transient DTOs.</summary>
    Task<IReadOnlyList<PublicFolderContact>> GetPublicFolderContactsAsync(string folderId, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>Whether this provider can browse the mailbox's online archive read-only (currently Exchange only).</summary>
    bool SupportsOnlineArchive => false;

    /// <summary>
    /// Direct children of an archive folder, or the archive's top-level folders when <paramref name="parentFolderId"/>
    /// is null. Returns null for the root call when the mailbox has no archive provisioned.
    /// </summary>
    Task<IReadOnlyList<PublicFolderNode>> GetOnlineArchiveChildrenAsync(string parentFolderId, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>A page of an archive folder's items as transient copies. A non-positive <paramref name="take"/> means the provider's default page.</summary>
    Task<IReadOnlyList<MailCopy>> GetOnlineArchiveMailItemsAsync(string folderId, int skip, int take, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    /// <summary>Raw MIME of a single archive message.</summary>
    Task<byte[]> GetOnlineArchiveMailMimeAsync(string folderId, string itemId, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();
}

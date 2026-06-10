using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Exchange.WebServices.Data;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// On-premises Exchange synchronizer over EWS (Exchange Web Services).
///
/// Phase 0/1 vertical slice: authenticates, ensures the Inbox folder exists, and
/// synchronizes Inbox item metadata via EWS SyncFolderItems (delta), persisting the
/// opaque per-folder sync-state in <see cref="MailItemFolder.DeltaToken"/>. MIME is
/// fetched on-demand. Full folder-hierarchy sync, all mail actions, and calendar
/// (TCalendarEventType = Appointment) come in later phases.
///
/// Note: the Exchange.WebServices.NETCore API is async — every EWS call is awaited.
/// </summary>
public class ExchangeSynchronizer : WinoSynchronizer<EwsRequest, Item, Appointment>
{
    public override uint BatchModificationSize => 100;
    public override uint InitialMessageDownloadCountPerFolder => 500;

    // Exchange2013_SP1 is the highest schema the EWS Managed API exposes; it negotiates
    // up against 2016/2019/SE. TODO: make per-account configurable for older servers.
    private const ExchangeVersion TargetExchangeVersion = ExchangeVersion.Exchange2013_SP1;

    private readonly ILogger _logger = Log.ForContext<ExchangeSynchronizer>();
    private readonly IExchangeAuthenticator _exchangeAuthenticator;
    private readonly IExchangeChangeProcessor _exchangeChangeProcessor;
    private readonly IExchangeSynchronizerErrorHandlerFactory _errorHandlerFactory;

    public ExchangeSynchronizer(MailAccount account,
                                IExchangeAuthenticator exchangeAuthenticator,
                                IExchangeChangeProcessor exchangeChangeProcessor,
                                IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory)
        : base(account, WeakReferenceMessenger.Default)
    {
        _exchangeAuthenticator = exchangeAuthenticator;
        _exchangeChangeProcessor = exchangeChangeProcessor;
        _errorHandlerFactory = errorHandlerFactory;
    }

    // Metadata loaded for each synced item. Accessing a property NOT in this set throws,
    // so every property read in MapToMailCopy must be listed here.
    private static readonly PropertySet ItemMetadataPropertySet = new(
        BasePropertySet.IdOnly,
        ItemSchema.Subject,
        ItemSchema.DateTimeReceived,
        ItemSchema.Size,
        ItemSchema.HasAttachments,
        ItemSchema.Importance,
        ItemSchema.ConversationId,
        EmailMessageSchema.From,
        EmailMessageSchema.IsRead,
        EmailMessageSchema.InternetMessageId);

    /// <summary>
    /// Builds an EWS service bound to the account's on-premises endpoint and credentials.
    /// EWS is stateless HTTP, so this is recreated per operation rather than pooled.
    /// </summary>
    private async Task<ExchangeService> CreateServiceAsync(TimeZoneInfo timeZone = null)
    {
        var serverInformation = Account.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        var credentials = await _exchangeAuthenticator.GetCredentialsAsync(Account).ConfigureAwait(false);

        // Records the endpoint and the identity we authenticate as, so connectivity issues can be
        // diagnosed (e.g. confirming the configured mailbox is used, not the logged-in Windows user).
        _logger.Debug(
            "Building EWS service. Url={Url}, OAuth={UseOAuth}, ConfiguredUser={User}, CredentialType={CredType}",
            serverInformation.IncomingServer,
            serverInformation.UseOAuthAuthentication,
            serverInformation.IncomingServerUsername,
            credentials?.GetType().Name);

        // The service time zone is constructor-only; calendar sync passes UTC so appointment times
        // come back as UTC and the change processor converts them to each event's own zone.
        return timeZone == null
            ? new ExchangeService(TargetExchangeVersion)
            {
                Credentials = credentials,
                Url = new Uri(serverInformation.IncomingServer)
            }
            : new ExchangeService(TargetExchangeVersion, timeZone)
            {
                Credentials = credentials,
                Url = new Uri(serverInformation.IncomingServer)
            };
    }

    public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(Item message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        var mailCopy = MapToMailCopy(message, assignedFolder);
        if (mailCopy == null)
            return Task.FromResult<List<NewMailItemPackage>>(null);

        // MIME is downloaded on-demand (DownloadMissingMimeMessageAsync), not during sync.
        var package = new NewMailItemPackage(mailCopy, null, assignedFolder.RemoteFolderId);
        return Task.FromResult<List<NewMailItemPackage>>([package]);
    }

    /// <summary>
    /// Best-effort alias/proxy-address discovery. EWS has no direct "my proxy addresses" call
    /// (unlike Graph's <c>proxyAddresses</c>), so this resolves the mailbox's own directory entry
    /// via <see cref="ExchangeService.ResolveName"/> and harvests any SMTP addresses it exposes.
    /// The primary address is always persisted as the root alias; directory lookup is wrapped so a
    /// failure (or a server that returns no extra addresses) degrades to root-alias-only rather than
    /// failing account setup.
    /// NOTE: directory results carry at most the three indexed addresses and may use non-SMTP
    /// routing; this is intentionally lenient and may need tuning against more deployments.
    /// </summary>
    protected override async Task SynchronizeAliasesAsync()
    {
        var aliases = new Dictionary<string, RemoteAccountAlias>(StringComparer.OrdinalIgnoreCase);

        // Directory addresses carry their routing type as a prefix ("SMTP:" for the primary,
        // "smtp:" for secondary proxies, "EUM:"/"X500:" for non-mail routes). Strip the prefix and
        // return null for anything that isn't a usable SMTP address.
        static string CleanSmtpAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return null;

            var normalized = address.Trim();

            var colonIndex = normalized.IndexOf(':');
            if (colonIndex >= 0)
            {
                if (!normalized[..colonIndex].Equals("SMTP", StringComparison.OrdinalIgnoreCase))
                    return null; // EX:, X500:, EUM:, etc.
                normalized = normalized[(colonIndex + 1)..].Trim();
            }

            return normalized.Contains('@') && normalized.Contains('.') ? normalized : null;
        }

        void AddAlias(string rawAddress)
        {
            var normalized = CleanSmtpAddress(rawAddress);
            if (normalized == null)
                return;

            var isAccountAddress = normalized.Equals(Account.Address, StringComparison.OrdinalIgnoreCase);

            if (aliases.TryGetValue(normalized, out var existing))
            {
                existing.IsPrimary |= isAccountAddress;
                existing.IsRootAlias |= isAccountAddress;
                return;
            }

            aliases[normalized] = new RemoteAccountAlias
            {
                AliasAddress = normalized,
                ReplyToAddress = normalized,
                IsPrimary = isAccountAddress,
                IsRootAlias = isAccountAddress,
                IsVerified = true,
                Source = AliasSource.ProviderDiscovered,
                // Discovered addresses all belong to this mailbox, so they are send-capable.
                SendCapability = AliasSendCapability.Confirmed
            };
        }

        // The mailbox's own primary address is always the root alias.
        AddAlias(Account.Address);

        try
        {
            var service = await CreateServiceAsync().ConfigureAwait(false);

            var resolutions = await service
                .ResolveName(Account.Address, ResolveNameSearchLocation.DirectoryOnly, returnContactDetails: true, CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var resolution in resolutions)
            {
                // Only harvest the directory entry that actually represents this mailbox — guards
                // against an ambiguous ResolveName returning other people's addresses.
                if (!string.Equals(CleanSmtpAddress(resolution.Mailbox?.Address), Account.Address, StringComparison.OrdinalIgnoreCase))
                    continue;

                AddAlias(resolution.Mailbox?.Address);

                var contact = resolution.Contact;
                if (contact?.EmailAddresses == null)
                    continue;

                foreach (var key in new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 })
                {
                    if (contact.EmailAddresses.TryGetValue(key, out var emailAddress))
                        AddAlias(emailAddress?.Address);
                }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: proxy-address discovery is best-effort. Keep the root alias and move on.
            _logger.Debug(ex, "Exchange alias (proxy-address) discovery via ResolveName failed for {Account}.", Account.Name);
        }

        await _exchangeChangeProcessor
            .UpdateRemoteAliasInformationAsync(Account, aliases.Values.ToList())
            .ConfigureAwait(false);
    }

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);

        // 1) Reconcile the folder tree, then 2) delta-sync items per synchronizable folder.
        await SynchronizeFoldersAsync(service, cancellationToken).ConfigureAwait(false);

        var localFolders = await _exchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var foldersToSync = localFolders
            .Where(f => f.IsSynchronizationEnabled && !string.IsNullOrEmpty(f.RemoteFolderId))
            .ToList();
        // TODO(Phase 1): honor options.Type / SynchronizationFolderIds for targeted sync.

        var downloaded = new List<MailCopy>();
        foreach (var folder in foldersToSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                downloaded.AddRange(await SynchronizeFolderItemsAsync(service, folder, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var errorContext = new SynchronizerErrorContext
                {
                    Account = Account,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    OperationType = "ExchangeFolderSync"
                };

                await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                CaptureSynchronizationIssue(errorContext);
                _logger.Error(ex, "Exchange folder sync failed for {Folder} ({Account}).", folder.FolderName, Account.Name);
            }
        }

        return MailSynchronizationResult.Completed(downloaded);
    }

    /// <summary>
    /// Reconciles the remote mail folder hierarchy into local MailItemFolders:
    /// inserts new folders, updates renamed/moved ones, and deletes folders no longer
    /// present remotely. Special-folder types are detected by binding well-known folders.
    /// </summary>
    private async Task SynchronizeFoldersAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var specialMap = await BuildSpecialFolderMapAsync(service).ConfigureAwait(false);

        var view = new FolderView(500)
        {
            Traversal = FolderTraversal.Deep,
            PropertySet = new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass, FolderSchema.ParentFolderId)
        };

        var remoteFolders = new List<Folder>();
        FindFoldersResults page;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = await service.FindFolders(WellKnownFolderName.MsgFolderRoot, view).ConfigureAwait(false);
            remoteFolders.AddRange(page.Folders.Where(IsMailFolder));
            if (page.NextPageOffset.HasValue)
                view.Offset = page.NextPageOffset.Value;
        }
        while (page.MoreAvailable);

        var localFolders = await _exchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var localByRemoteId = localFolders
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .GroupBy(f => f.RemoteFolderId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var remote in remoteFolders)
        {
            var remoteId = remote.Id.UniqueId;
            specialMap.TryGetValue(remoteId, out var specialType);

            if (localByRemoteId.TryGetValue(remoteId, out var existing))
            {
                existing.FolderName = remote.DisplayName;
                existing.ParentRemoteFolderId = remote.ParentFolderId?.UniqueId;
                existing.SpecialFolderType = specialType;
                await _exchangeChangeProcessor.UpdateFolderAsync(existing).ConfigureAwait(false);
            }
            else
            {
                await _exchangeChangeProcessor.InsertFolderAsync(new MailItemFolder
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = Account.Id,
                    RemoteFolderId = remoteId,
                    ParentRemoteFolderId = remote.ParentFolderId?.UniqueId,
                    FolderName = remote.DisplayName,
                    SpecialFolderType = specialType,
                    IsSticky = specialType != SpecialFolderType.Other,
                    IsSystemFolder = specialType != SpecialFolderType.Other,
                    IsSynchronizationEnabled = true,
                    ShowUnreadCount = specialType != SpecialFolderType.Deleted,
                }).ConfigureAwait(false);
            }
        }

        // Remove local folders that no longer exist remotely.
        var remoteIds = remoteFolders.Select(f => f.Id.UniqueId).ToHashSet();
        foreach (var local in localFolders)
        {
            if (!string.IsNullOrEmpty(local.RemoteFolderId) && !remoteIds.Contains(local.RemoteFolderId))
                await _exchangeChangeProcessor.DeleteFolderAsync(Account.Id, local.RemoteFolderId).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, SpecialFolderType>> BuildSpecialFolderMapAsync(ExchangeService service)
    {
        var wellKnown = new (WellKnownFolderName Folder, SpecialFolderType Special)[]
        {
            (WellKnownFolderName.Inbox, SpecialFolderType.Inbox),
            (WellKnownFolderName.SentItems, SpecialFolderType.Sent),
            (WellKnownFolderName.Drafts, SpecialFolderType.Draft),
            (WellKnownFolderName.DeletedItems, SpecialFolderType.Deleted),
            (WellKnownFolderName.JunkEmail, SpecialFolderType.Junk),
        };

        var map = new Dictionary<string, SpecialFolderType>();
        foreach (var (folder, special) in wellKnown)
        {
            try
            {
                var bound = await Folder.Bind(service, folder, new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                map[bound.Id.UniqueId] = special;
            }
            catch
            {
                // Folder may not exist on this mailbox; skip.
            }
        }

        return map;
    }

    // Mail folders carry the IPF.Note message class; skip calendar/contact/task/etc.
    private static bool IsMailFolder(Folder folder)
        => !string.IsNullOrEmpty(folder.FolderClass)
           && folder.FolderClass.StartsWith("IPF.Note", StringComparison.OrdinalIgnoreCase);

    private async Task<List<MailCopy>> SynchronizeFolderItemsAsync(ExchangeService service, MailItemFolder folder, CancellationToken cancellationToken)
    {
        var downloaded = new List<MailCopy>();
        var syncState = folder.DeltaToken;
        var folderId = new FolderId(folder.RemoteFolderId);
        bool moreAvailable;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changes = await service.SyncFolderItems(folderId, ItemMetadataPropertySet, null,
                (int)InitialMessageDownloadCountPerFolder, SyncFolderItemsScope.NormalItems, syncState).ConfigureAwait(false);

            foreach (var change in changes)
            {
                switch (change.ChangeType)
                {
                    case ChangeType.Create:
                    case ChangeType.Update:
                        if (change.Item == null) break;
                        var packages = await CreateNewMailPackagesAsync(change.Item, folder, cancellationToken).ConfigureAwait(false);
                        if (packages?.Count > 0)
                        {
                            // TODO(Phase 1): Update should reconcile in place rather than re-insert.
                            await _exchangeChangeProcessor.CreateMailsAsync(Account.Id, packages).ConfigureAwait(false);
                            downloaded.AddRange(packages.Select(p => p.Copy));
                        }
                        break;
                    case ChangeType.Delete:
                        await _exchangeChangeProcessor.DeleteMailsAsync(Account.Id, [change.ItemId.UniqueId]).ConfigureAwait(false);
                        break;
                    case ChangeType.ReadFlagChange:
                        await _exchangeChangeProcessor.ChangeMailReadStatusAsync(change.ItemId.UniqueId, change.IsRead).ConfigureAwait(false);
                        break;
                }
            }

            syncState = changes.SyncState;
            moreAvailable = changes.MoreChangesAvailable;
        }
        while (moreAvailable);

        folder.DeltaToken = syncState;
        await _exchangeChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);

        return downloaded;
    }

    private MailCopy MapToMailCopy(Item item, MailItemFolder assignedFolder)
    {
        if (item == null)
            return null;

        var email = item as EmailMessage;

        return new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Id = item.Id.UniqueId,
            FolderId = assignedFolder.Id,
            ThreadId = item.ConversationId?.UniqueId,
            MessageId = email?.InternetMessageId,
            Subject = item.Subject,
            FromName = email?.From?.Name,
            FromAddress = email?.From?.Address,
            CreationDate = item.DateTimeReceived.ToUniversalTime(),
            IsRead = email?.IsRead ?? true,
            HasAttachments = item.HasAttachments,
            Importance = MapImportance(item.Importance),
        };
    }

    private static MailImportance MapImportance(Importance importance) => importance switch
    {
        Importance.High => MailImportance.High,
        Importance.Low => MailImportance.Low,
        _ => MailImportance.Normal,
    };

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem, MailKit.ITransferProgress transferProgress = null, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);

        var ewsItem = await Item.Bind(service, new ItemId(mailItem.Id),
            new PropertySet(ItemSchema.MimeContent)).ConfigureAwait(false);

        using var stream = new MemoryStream(ewsItem.MimeContent.Content);
        var mimeMessage = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);

        await _exchangeChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
    }

    #region Mail & Folder Operations

    // Wraps an EWS operation into a single request bundle (EWS is stateless HTTP — one
    // service handles the whole batch, so per-action batching collapses to one bundle).
    private static List<IRequestBundle<EwsRequest>> Bundle(Func<ExchangeService, Task> action, IRequestBase request, IUIChangeRequest uiChangeRequest)
        => [new EwsRequestBundle(new EwsRequest((service, _) => action(service), request), request, uiChangeRequest)];

    public override List<IRequestBundle<EwsRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var isRead = requests[0].IsRead;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(async service =>
        {
            foreach (var id in ids)
            {
                var message = await EmailMessage.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, EmailMessageSchema.IsRead)).ConfigureAwait(false);
                if (message.IsRead == isRead) continue;
                message.IsRead = isRead;
                await message.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeFlag(BatchChangeFlagRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var flagged = requests[0].IsFlagged;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(async service =>
        {
            foreach (var id in ids)
            {
                var item = await Item.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, ItemSchema.Flag)).ConfigureAwait(false);
                item.Flag = new Flag { FlagStatus = flagged ? ItemFlagStatus.Flagged : ItemFlagStatus.NotFlagged };
                await item.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Move(BatchMoveRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var destination = new FolderId(requests[0].ToFolder.RemoteFolderId);
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(service => service.MoveItems(ids, destination), requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Delete(BatchDeleteRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(
            service => service.DeleteItems(ids, DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences),
            requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Archive(BatchArchiveRequest request)
        => Move(new BatchMoveRequest(request.Select(a => new MoveRequest(a.Item, a.FromFolder, a.ToFolder))));

    public override List<IRequestBundle<EwsRequest>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a))));

    public override List<IRequestBundle<EwsRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    // Drafts stay local until sent (same as IMAP — CreateDraft is intentionally not overridden).
    // Send serializes the composed MIME and submits it via EWS, optionally saving to Sent Items.
    public override List<IRequestBundle<EwsRequest>> SendDraft(SendDraftRequest request)
    {
        var preparation = request.Request;

        return Bundle(async service =>
        {
            var mime = preparation.Mime;

            // Strip the local-draft marker so it never leaks to recipients.
            mime.Headers.Remove(Wino.Core.Domain.Constants.WinoLocalDraftHeader);

            using var stream = new MemoryStream();
            await mime.WriteToAsync(stream).ConfigureAwait(false);

            var message = new EmailMessage(service)
            {
                MimeContent = new Microsoft.Exchange.WebServices.Data.MimeContent("UTF-8", stream.ToArray())
            };

            var saveToSent = preparation.AccountPreferences?.ShouldAppendMessagesToSentFolder == true
                             && preparation.SentFolder != null;

            if (saveToSent)
                await message.SendAndSaveCopy(new FolderId(preparation.SentFolder.RemoteFolderId)).ConfigureAwait(false);
            else
                await message.Send().ConfigureAwait(false);
        }, request, request);
    }

    // Drafts are pushed to the server Drafts folder (parity with IMAP). The local draft is
    // mapped to the server-assigned id so later sync/send reconcile against the same item.
    public override List<IRequestBundle<EwsRequest>> CreateDraft(CreateDraftRequest request)
    {
        var preparation = request.DraftPreperationRequest;
        var draftsFolderId = preparation.CreatedLocalDraftCopy.AssignedFolder.RemoteFolderId;

        return Bundle(async service =>
        {
            using var stream = new MemoryStream();
            await preparation.CreatedLocalDraftMimeMessage.WriteToAsync(stream).ConfigureAwait(false);

            var message = new EmailMessage(service)
            {
                MimeContent = new Microsoft.Exchange.WebServices.Data.MimeContent("UTF-8", stream.ToArray())
            };

            await message.Save(new FolderId(draftsFolderId)).ConfigureAwait(false);

            await _exchangeChangeProcessor.MapLocalDraftAsync(
                Account.Id,
                preparation.CreatedLocalDraftCopy.UniqueId,
                message.Id.UniqueId,
                message.Id.UniqueId,
                preparation.CreatedLocalDraftCopy.ThreadId).ConfigureAwait(false);
        }, request, request);
    }

    #endregion

    #region Calendar

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            // Appointment times are read in UTC; the change processor converts to the event's own zone.
            var service = await CreateServiceAsync(TimeZoneInfo.Utc).ConfigureAwait(false);

            // 1) Reconcile the set of calendars (AccountCalendar) against the server.
            await SynchronizeCalendarsAsync(service, cancellationToken).ConfigureAwait(false);

            // 2) Read events per enabled calendar over a moving window (CalendarView expands occurrences).
            var windowStartUtc = DateTime.UtcNow.AddMonths(-CalendarWindowPastMonths);
            var windowEndUtc = DateTime.UtcNow.AddMonths(CalendarWindowFutureMonths);

            var calendars = (await _exchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false))
                .Where(c => c.IsSynchronizationEnabled)
                .ToList();

            foreach (var calendar in calendars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SynchronizeCalendarEventsForCalendarAsync(service, calendar, windowStartUtc, windowEndUtc, cancellationToken).ConfigureAwait(false);
            }

            return CalendarSynchronizationResult.Empty;
        }
        catch (OperationCanceledException)
        {
            return CalendarSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "EWS calendar synchronization failed for {Address}.", Account.Address);
            return CalendarSynchronizationResult.Failed(ex);
        }
    }

    /// <summary>
    /// Discovers the server's calendar folders (the default Calendar plus any IPF.Appointment folders)
    /// and reconciles them against the locally stored <see cref="AccountCalendar"/> rows.
    /// </summary>
    private async Task SynchronizeCalendarsAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var properties = new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass);

        var defaultCalendar = await CalendarFolder.Bind(service, WellKnownFolderName.Calendar, properties).ConfigureAwait(false);

        var view = new FolderView(1000) { Traversal = FolderTraversal.Deep, PropertySet = properties };
        var appointmentFolders = await service
            .FindFolders(WellKnownFolderName.MsgFolderRoot, new SearchFilter.IsEqualTo(FolderSchema.FolderClass, "IPF.Appointment"), view)
            .ConfigureAwait(false);

        // Build the remote calendar set keyed by id, always including the default calendar.
        var remoteFolders = new Dictionary<string, Folder> { [defaultCalendar.Id.UniqueId] = defaultCalendar };
        foreach (var folder in appointmentFolders.Folders)
            remoteFolders[folder.Id.UniqueId] = folder;

        var localCalendars = await _exchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        // Remove calendars that no longer exist on the server.
        foreach (var local in localCalendars)
        {
            if (!remoteFolders.ContainsKey(local.RemoteCalendarId))
                await _exchangeChangeProcessor.DeleteAccountCalendarAsync(local).ConfigureAwait(false);
        }

        // Insert new calendars and update changed ones.
        foreach (var (remoteId, folder) in remoteFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isPrimary = remoteId == defaultCalendar.Id.UniqueId;
            var name = string.IsNullOrWhiteSpace(folder.DisplayName) ? "Calendar" : folder.DisplayName;

            var existing = localCalendars.FirstOrDefault(c => c.RemoteCalendarId == remoteId);
            if (existing == null)
            {
                await _exchangeChangeProcessor.InsertAccountCalendarAsync(BuildAccountCalendar(remoteId, name, isPrimary)).ConfigureAwait(false);
            }
            else if (existing.Name != name || existing.IsPrimary != isPrimary)
            {
                existing.Name = name;
                existing.IsPrimary = isPrimary;
                await _exchangeChangeProcessor.UpdateAccountCalendarAsync(existing).ConfigureAwait(false);
            }
        }
    }

    private AccountCalendar BuildAccountCalendar(string remoteCalendarId, string name, bool isPrimary)
        => new()
        {
            Id = Guid.NewGuid(),
            AccountId = Account.Id,
            RemoteCalendarId = remoteCalendarId,
            Name = name,
            IsPrimary = isPrimary,
            IsSynchronizationEnabled = true,
            IsReadOnly = false,
            DefaultShowAs = CalendarItemShowAs.Busy,
            TextColorHex = "#FFFFFF",
            BackgroundColorHex = string.IsNullOrWhiteSpace(Account.AccountColorHex) ? "#2564CF" : Account.AccountColorHex,
            TimeZone = TimeZoneInfo.Local.Id
        };

    // Read window for calendar events. CalendarView expands recurrences into occurrences, so a moving
    // window (re-fetched + reconciled each sync) bounds occurrence volume without needing delta tokens.
    private const int CalendarWindowPastMonths = 3;
    private const int CalendarWindowFutureMonths = 12;

    private static readonly PropertySet EventPropertySet = new(
        BasePropertySet.IdOnly,
        ItemSchema.Subject, ItemSchema.Body, ItemSchema.Sensitivity,
        ItemSchema.DateTimeCreated, ItemSchema.LastModifiedTime,
        AppointmentSchema.Start, AppointmentSchema.End, AppointmentSchema.Location,
        AppointmentSchema.Organizer, AppointmentSchema.LegacyFreeBusyStatus,
        AppointmentSchema.IsReminderSet, AppointmentSchema.ReminderMinutesBeforeStart,
        AppointmentSchema.RequiredAttendees, AppointmentSchema.OptionalAttendees,
        AppointmentSchema.MyResponseType, AppointmentSchema.IsAllDayEvent,
        AppointmentSchema.StartTimeZone, AppointmentSchema.EndTimeZone,
        ExchangeCalendarSchema.WinoClientTrackingId)
    { RequestedBodyType = BodyType.HTML };

    private async Task SynchronizeCalendarEventsForCalendarAsync(ExchangeService service, AccountCalendar calendar, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
    {
        var calendarFolder = await CalendarFolder
            .Bind(service, new FolderId(calendar.RemoteCalendarId), new PropertySet(BasePropertySet.IdOnly))
            .ConfigureAwait(false);

        var seenRemoteIds = new HashSet<string>();

        // Page the window in chunks so a dense recurring series can't exceed the server's per-view cap.
        var chunkStartUtc = windowStartUtc;
        while (chunkStartUtc < windowEndUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkEndUtc = chunkStartUtc.AddMonths(3);
            if (chunkEndUtc > windowEndUtc)
                chunkEndUtc = windowEndUtc;

            var view = new CalendarView(chunkStartUtc, chunkEndUtc, 1000) { PropertySet = new PropertySet(BasePropertySet.IdOnly) };
            var results = await calendarFolder.FindAppointments(view).ConfigureAwait(false);
            var appointments = results.Items;

            if (appointments.Count > 0)
            {
                await service.LoadPropertiesForItems(appointments, EventPropertySet).ConfigureAwait(false);

                foreach (var appointment in appointments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    seenRemoteIds.Add(appointment.Id.UniqueId);
                    await _exchangeChangeProcessor.ManageCalendarEventAsync(appointment, calendar, Account).ConfigureAwait(false);
                }
            }

            if (results.MoreAvailable)
                Log.Warning("Calendar window chunk for {Calendar} hit the result cap; some events may be missing.", calendar.Name);

            chunkStartUtc = chunkEndUtc;
        }

        // Reconcile: drop locally stored events in this window that the server no longer returns.
        var localEvents = await _exchangeChangeProcessor.GetCalendarItemsInRangeAsync(calendar, windowStartUtc, windowEndUtc).ConfigureAwait(false);
        foreach (var local in localEvents)
        {
            // Stored RemoteEventIds may carry a Wino client-tracking suffix; compare on the raw id.
            if (!string.IsNullOrEmpty(local.RemoteEventId) &&
                !seenRemoteIds.Contains(local.RemoteEventId.GetProviderRemoteEventId()))
            {
                await _exchangeChangeProcessor.DeleteCalendarItemAsync(local.Id).ConfigureAwait(false);
            }
        }
    }

    #endregion

    #region Calendar Operations

    public override List<IRequestBundle<EwsRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var item = request.PreparedItem;
        var attendees = request.ComposeResult?.Attendees;
        var reminders = request.ComposeResult?.SelectedReminders;
        var calendarRemoteId = request.AssignedCalendar.RemoteCalendarId;
        var isRecurring = request.IsRecurring;
        var title = item.Title;

        return Bundle(async service =>
        {
            var appointment = new Appointment(service);
            ApplyCalendarItem(appointment, item, attendees, reminders);

            // Stamp the local preview id so the synced event reconciles with the optimistic UI item.
            appointment.SetExtendedProperty(ExchangeCalendarSchema.WinoClientTrackingId, item.Id.ToString("N"));

            // RRULE -> EWS recurrence mapping is a follow-up; create the base event for now.
            if (isRecurring)
                Log.Warning("EWS recurring-event creation is not yet supported; created '{Title}' as a single event.", title);

            var sendMode = HasAttendees(appointment) ? SendInvitationsMode.SendToAllAndSaveCopy : SendInvitationsMode.SendToNone;
            await appointment.Save(new FolderId(calendarRemoteId), sendMode).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var item = request.Item;
        var attendees = request.Attendees;

        return Bundle(async service =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(item.RemoteEventId.GetProviderRemoteEventId())).ConfigureAwait(false);
            ApplyCalendarItem(appointment, item, attendees, reminders: null);

            var sendMode = HasAttendees(appointment)
                ? SendInvitationsOrCancellationsMode.SendToAllAndSaveCopy
                : SendInvitationsOrCancellationsMode.SendToNone;
            await appointment.Update(ConflictResolutionMode.AutoResolve, sendMode).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        => UpdateCalendarEvent(request);

    public override List<IRequestBundle<EwsRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
    {
        var remoteId = request.Item.RemoteEventId;

        return Bundle(async service =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(remoteId.GetProviderRemoteEventId()), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
            await appointment.Delete(DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> AcceptEvent(AcceptEventRequest request)
        => RespondToEvent(request, RsvpResponse.Accept, request.Item.RemoteEventId, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> TentativeEvent(TentativeEventRequest request)
        => RespondToEvent(request, RsvpResponse.Tentative, request.Item.RemoteEventId, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> DeclineEvent(DeclineEventRequest request)
        => RespondToEvent(request, RsvpResponse.Decline, request.Item.RemoteEventId, request.ResponseMessage);

    private enum RsvpResponse { Accept, Tentative, Decline }

    private List<IRequestBundle<EwsRequest>> RespondToEvent(CalendarRequestBase request, RsvpResponse response, string remoteEventId, string responseMessage)
    {
        return Bundle(async service =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(remoteEventId.GetProviderRemoteEventId()), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);

            switch (response)
            {
                case RsvpResponse.Accept:
                {
                    var message = appointment.CreateAcceptMessage(false);
                    if (!string.IsNullOrEmpty(responseMessage)) message.Body = new MessageBody(responseMessage);
                    await message.SendAndSaveCopy().ConfigureAwait(false);
                    break;
                }
                case RsvpResponse.Tentative:
                {
                    var message = appointment.CreateAcceptMessage(true);
                    if (!string.IsNullOrEmpty(responseMessage)) message.Body = new MessageBody(responseMessage);
                    await message.SendAndSaveCopy().ConfigureAwait(false);
                    break;
                }
                case RsvpResponse.Decline:
                {
                    var message = appointment.CreateDeclineMessage();
                    if (!string.IsNullOrEmpty(responseMessage)) message.Body = new MessageBody(responseMessage);
                    await message.SendAndSaveCopy().ConfigureAwait(false);
                    break;
                }
            }
        }, request, request);
    }

    private static bool HasAttendees(Appointment appointment)
        => appointment.RequiredAttendees.Count > 0 || appointment.OptionalAttendees.Count > 0;

    private static void ApplyCalendarItem(Appointment appointment, CalendarItem item, List<CalendarEventAttendee> attendees, List<Reminder> reminders)
    {
        appointment.Subject = item.Title;
        appointment.Body = new MessageBody(BodyType.HTML, item.Description ?? string.Empty);
        appointment.Location = item.Location;

        // item.StartDate is wall-clock in item.StartTimeZone; set the appointment's time zone so EWS
        // interprets the wall-clock time correctly regardless of the service time zone.
        var timeZone = ResolveTimeZoneInfo(item.StartTimeZone);
        appointment.StartTimeZone = timeZone;
        appointment.EndTimeZone = timeZone;
        appointment.Start = item.StartDate;
        appointment.End = item.StartDate.AddSeconds(item.DurationInSeconds);
        appointment.IsAllDayEvent = item.IsAllDayEvent;

        appointment.LegacyFreeBusyStatus = item.ShowAs switch
        {
            CalendarItemShowAs.Free => LegacyFreeBusyStatus.Free,
            CalendarItemShowAs.Tentative => LegacyFreeBusyStatus.Tentative,
            CalendarItemShowAs.OutOfOffice => LegacyFreeBusyStatus.OOF,
            CalendarItemShowAs.WorkingElsewhere => LegacyFreeBusyStatus.WorkingElsewhere,
            _ => LegacyFreeBusyStatus.Busy
        };

        if (attendees != null)
        {
            appointment.RequiredAttendees.Clear();
            appointment.OptionalAttendees.Clear();

            foreach (var attendee in attendees)
            {
                if (string.IsNullOrEmpty(attendee.Email))
                    continue;

                if (attendee.IsOptionalAttendee)
                    appointment.OptionalAttendees.Add(attendee.Email);
                else
                    appointment.RequiredAttendees.Add(attendee.Email);
            }
        }

        if (reminders != null && reminders.Count > 0)
        {
            appointment.IsReminderSet = true;
            appointment.ReminderMinutesBeforeStart = (int)Math.Max(0, reminders[0].DurationInSeconds / 60);
        }
    }

    private static TimeZoneInfo ResolveTimeZoneInfo(string ianaTimeZone)
    {
        if (string.IsNullOrEmpty(ianaTimeZone))
            return TimeZoneInfo.Utc;

        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZone, out var windowsId))
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
        catch
        {
            // Fall through to the direct lookup / UTC.
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    #endregion

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<EwsRequest>> batchedRequests, CancellationToken cancellationToken = default)
    {
        if (batchedRequests == null || batchedRequests.Count == 0)
            return;

        // Apply optimistic local UI changes before hitting the server (matches IMAP/Outlook).
        ApplyOptimisticUiChanges(batchedRequests);

        var service = await CreateServiceAsync().ConfigureAwait(false);
        var errors = new List<string>();

        foreach (var bundle in batchedRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await bundle.NativeRequest.IntegratorTask(service, bundle.NativeRequest.Request).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await HandleFailedRequestAsync(bundle, ex, errors).ConfigureAwait(false);
            }
        }

        // Surface unhandled / transient failures so the sync is marked failed (and retried).
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// Routes a failed request through the error-handler factory (mirrors OutlookSynchronizer):
    /// entity-not-found and auth failures are owned by their handlers; transient and unhandled
    /// errors are reverted, captured, and surfaced so the operation can be retried.
    /// </summary>
    private async Task HandleFailedRequestAsync(IRequestBundle<EwsRequest> bundle, Exception exception, List<string> errors)
    {
        var errorContext = new SynchronizerErrorContext
        {
            Account = Account,
            ErrorMessage = exception.Message,
            Exception = exception,
            RequestBundle = bundle,
            Request = bundle.Request,
            IsEntityNotFound = IsEwsEntityNotFound(exception, bundle.UIChangeRequest),
            OperationType = "ExchangeExecuteRequest"
        };

        var handled = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

        // A handled, non-transient error (entity-not-found cleanup, auth-required) is owned by
        // the handler — it has already reverted/reconciled. Everything else is reverted here.
        if (!handled || errorContext.Severity == SynchronizerErrorSeverity.Transient)
        {
            CaptureSynchronizationIssue(errorContext);
            bundle.UIChangeRequest?.RevertUIChanges();
            _logger.Error(exception, "Exchange request execution failed for {Account}.", Account.Name);
            errors.Add(exception.Message);
        }
    }

    // True when an EWS failure indicates the remote item/folder no longer exists for an
    // operation that targets an existing entity — lets EntityNotFoundHandler reconcile locally.
    private static bool IsEwsEntityNotFound(Exception exception, IUIChangeRequest uiChangeRequest)
    {
        if (uiChangeRequest == null || !IsExistingEntityOperation(uiChangeRequest))
            return false;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is ServiceResponseException serviceResponse &&
                serviceResponse.ErrorCode is ServiceError.ErrorItemNotFound
                    or ServiceError.ErrorFolderNotFound
                    or ServiceError.ErrorNonExistentMailbox)
            {
                return true;
            }

            var message = current.Message?.ToLowerInvariant() ?? string.Empty;
            if (message.Contains("not found") || message.Contains("does not exist") || message.Contains("cannot be found"))
                return true;
        }

        return false;
    }

    private static bool IsExistingEntityOperation(IUIChangeRequest request)
        => request is BatchDeleteRequest or BatchMoveRequest or BatchChangeFlagRequest
            or BatchMarkReadRequest or BatchArchiveRequest
            or DeleteRequest or MoveRequest or ChangeFlagRequest or MarkReadRequest;
}

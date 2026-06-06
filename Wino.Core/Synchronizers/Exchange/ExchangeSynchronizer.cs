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
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
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
    private async Task<ExchangeService> CreateServiceAsync()
    {
        var serverInformation = Account.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        var credentials = await _exchangeAuthenticator.GetCredentialsAsync(Account).ConfigureAwait(false);

        return new ExchangeService(TargetExchangeVersion)
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
        return Task.FromResult(new List<NewMailItemPackage> { package });
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
            downloaded.AddRange(await SynchronizeFolderItemsAsync(service, folder, cancellationToken).ConfigureAwait(false));
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

        var view = new FolderView(500) { Traversal = FolderTraversal.Deep };
        view.PropertySet = new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass, FolderSchema.ParentFolderId);

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
                        await _exchangeChangeProcessor.DeleteMailsAsync(Account.Id, new[] { change.ItemId.UniqueId }).ConfigureAwait(false);
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

    #endregion

    protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        // TODO(Phase 2): EWS CalendarFolder / Appointment synchronization.
        return Task.FromResult(CalendarSynchronizationResult.Empty);
    }

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<EwsRequest>> batchedRequests, CancellationToken cancellationToken = default)
    {
        if (batchedRequests == null || batchedRequests.Count == 0)
            return;

        // Apply optimistic local UI changes before hitting the server (matches IMAP/Outlook).
        ApplyOptimisticUiChanges(batchedRequests);

        var service = await CreateServiceAsync().ConfigureAwait(false);

        foreach (var bundle in batchedRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await bundle.NativeRequest.IntegratorTask(service, bundle.NativeRequest.Request).ConfigureAwait(false);
        }
    }
}

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

        // Vertical slice: ensure the Inbox exists locally, then delta-sync its items.
        var inboxFolder = await EnsureInboxFolderAsync(service, cancellationToken).ConfigureAwait(false);
        var downloaded = await SynchronizeFolderItemsAsync(service, inboxFolder, cancellationToken).ConfigureAwait(false);

        return MailSynchronizationResult.Completed(downloaded);
    }

    private async Task<MailItemFolder> EnsureInboxFolderAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var localFolders = await _exchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var inbox = localFolders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Inbox);

        if (inbox != null)
            return inbox;

        var ewsInbox = await Folder.Bind(service, WellKnownFolderName.Inbox,
            new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName)).ConfigureAwait(false);

        inbox = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = Account.Id,
            RemoteFolderId = ewsInbox.Id.UniqueId,
            FolderName = ewsInbox.DisplayName,
            SpecialFolderType = SpecialFolderType.Inbox,
            IsSystemFolder = true,
            IsSticky = true,
            IsSynchronizationEnabled = true,
            ShowUnreadCount = true,
        };

        await _exchangeChangeProcessor.InsertFolderAsync(inbox).ConfigureAwait(false);
        return inbox;
    }

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

    protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        // TODO(Phase 2): EWS CalendarFolder / Appointment synchronization.
        return Task.FromResult(CalendarSynchronizationResult.Empty);
    }

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<EwsRequest>> batchedRequests, CancellationToken cancellationToken = default)
    {
        if (batchedRequests == null || batchedRequests.Count == 0)
            return;

        var service = await CreateServiceAsync().ConfigureAwait(false);

        foreach (var bundle in batchedRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await bundle.NativeRequest.IntegratorTask(service, bundle.NativeRequest.Request).ConfigureAwait(false);
        }
    }
}

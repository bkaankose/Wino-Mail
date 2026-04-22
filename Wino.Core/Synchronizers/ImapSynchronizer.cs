using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Integration;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Misc;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Services.Extensions;

namespace Wino.Core.Synchronizers.Mail;

public class ImapSynchronizer : WinoSynchronizer<ImapRequest, ImapMessageCreationPackage, object>, IImapSynchronizer
{
    /// <summary>
    /// N/A for IMAP as it doesn't support batch modifications natively.
    /// </summary>
    public override uint BatchModificationSize => 1000;
    public override uint InitialMessageDownloadCountPerFolder => 500;

    #region Idle Implementation

    private static readonly Random IdleReconnectJitter = new();
    private readonly object _idleDebounceLock = new();
    private CancellationTokenSource _idleLoopCancellationTokenSource;
    private Task _idleLoopTask;
    private int _lastIdleInboxCount = -1;
    private DateTime _lastIdleSyncRequestUtc = DateTime.MinValue;
    private readonly TimeSpan _idleSyncDebounceWindow = TimeSpan.FromSeconds(15);

    #endregion

    private readonly ILogger _logger = Log.ForContext<ImapSynchronizer>();
    private readonly ImapClientPool _clientPool;
    private readonly IImapChangeProcessor _imapChangeProcessor;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly UnifiedImapSynchronizer _unifiedSynchronizer;
    private readonly IImapSynchronizerErrorHandlerFactory _errorHandlerFactory;
    private readonly ICalDavClient _calDavClient;
    private readonly IAutoDiscoveryService _autoDiscoveryService;
    private readonly ICalendarService _calendarService;
    private readonly SemaphoreSlim _calDavDiscoveryLock = new(1, 1);
    private Uri _cachedCalDavServiceUri;
    private bool _isCalDavDiscoveryAttempted;
    private readonly IImapCalendarOperationHandler _localCalendarOperationHandler;
    private readonly IImapCalendarOperationHandler _calDavCalendarOperationHandler;
    private bool _isFolderStructureChanged;

    public ImapSynchronizer(MailAccount account,
                            IImapChangeProcessor imapChangeProcessor,
                            IApplicationConfiguration applicationConfiguration,
                            UnifiedImapSynchronizer unifiedSynchronizer,
                            IImapSynchronizerErrorHandlerFactory errorHandlerFactory,
                            ICalDavClient calDavClient,
                            IAutoDiscoveryService autoDiscoveryService,
                            ICalendarService calendarService) : base(account, WeakReferenceMessenger.Default)
    {
        _imapChangeProcessor = imapChangeProcessor;
        _applicationConfiguration = applicationConfiguration;
        _unifiedSynchronizer = unifiedSynchronizer;
        _errorHandlerFactory = errorHandlerFactory;
        _calDavClient = calDavClient;
        _autoDiscoveryService = autoDiscoveryService;
        _calendarService = calendarService;

        var poolOptions = ImapClientPoolOptions.CreateDefault(Account.ServerInformation);

        _clientPool = new ImapClientPool(poolOptions);
        _localCalendarOperationHandler = new LocalCalendarOperationHandler(Account, _imapChangeProcessor, _calendarService, _applicationConfiguration.ApplicationDataFolderPath, "local");
        _calDavCalendarOperationHandler = new CalDavCalendarOperationHandler(this, Account, _calendarService, _calDavClient);
    }

    /// <summary>
    /// Returns UniqueId for the given mail copy id.
    /// </summary>
    private UniqueId GetUniqueId(string mailCopyId) => new(MailkitClientExtensions.ResolveUid(mailCopyId));

    #region Mail Integrations

    // Items are grouped before being passed to this method.
    // Meaning that all items will come from and to the same folder.
    // It's fine to assume that here.

    public override List<IRequestBundle<ImapRequest>> Move(BatchMoveRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return CreateSingleTaskBundle(async (client, _) =>
        {
            var sourceFolder = await client.GetFolderAsync(requests[0].FromFolder.RemoteFolderId).ConfigureAwait(false);
            var destinationFolder = await client.GetFolderAsync(requests[0].ToFolder.RemoteFolderId).ConfigureAwait(false);
            var uniqueIds = requests.Select(item => GetUniqueId(item.Item.Id)).ToList();

            await sourceFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            try
            {
                await sourceFolder.MoveToAsync(uniqueIds, destinationFolder).ConfigureAwait(false);
            }
            finally
            {
                await sourceFolder.CloseAsync().ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<ImapRequest>> ChangeFlag(BatchChangeFlagRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return CreateSingleTaskBundle(async (client, _) =>
        {
            var folder = requests[0].Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId).ConfigureAwait(false);
            var uniqueIds = requests.Select(item => GetUniqueId(item.Item.Id)).ToList();
            var request = new StoreFlagsRequest(requests[0].IsFlagged ? StoreAction.Add : StoreAction.Remove, MessageFlags.Flagged)
            {
                Silent = true
            };

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            try
            {
                await remoteFolder.StoreAsync(uniqueIds, request).ConfigureAwait(false);
            }
            finally
            {
                await remoteFolder.CloseAsync().ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<ImapRequest>> Delete(BatchDeleteRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return CreateSingleTaskBundle(async (client, _) =>
        {
            var folder = requests[0].Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId).ConfigureAwait(false);
            var uniqueIds = requests.Select(request => GetUniqueId(request.Item.Id)).ToList();
            var storeRequest = new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true };

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            try
            {
                await remoteFolder.StoreAsync(uniqueIds, storeRequest).ConfigureAwait(false);
                await remoteFolder.ExpungeAsync(uniqueIds).ConfigureAwait(false);
            }
            finally
            {
                await remoteFolder.CloseAsync().ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<ImapRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return CreateSingleTaskBundle(async (client, _) =>
        {
            var folder = requests[0].Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId).ConfigureAwait(false);
            var uniqueIds = requests.Select(request => GetUniqueId(request.Item.Id)).ToList();
            var storeRequest = new StoreFlagsRequest(requests[0].IsRead ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen)
            {
                Silent = true
            };

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            try
            {
                await remoteFolder.StoreAsync(uniqueIds, storeRequest).ConfigureAwait(false);
            }
            finally
            {
                await remoteFolder.CloseAsync().ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<ImapRequest>> CreateDraft(CreateDraftRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            var remoteDraftFolder = await client.GetFolderAsync(request.DraftPreperationRequest.CreatedLocalDraftCopy.AssignedFolder.RemoteFolderId).ConfigureAwait(false);

            await remoteDraftFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            await remoteDraftFolder.AppendAsync(request.DraftPreperationRequest.CreatedLocalDraftMimeMessage, MessageFlags.Draft).ConfigureAwait(false);
            await remoteDraftFolder.CloseAsync().ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<ImapRequest>> Archive(BatchArchiveRequest request)
    {
        var batchMoveRequest = new BatchMoveRequest(request.Select(item => new MoveRequest(item.Item, item.FromFolder, item.ToFolder)));
        return Move(batchMoveRequest);
    }


    public override List<IRequestBundle<ImapRequest>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a))));

    public override List<IRequestBundle<ImapRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    public override List<IRequestBundle<ImapRequest>> SendDraft(SendDraftRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            // Batch sending is not supported. It will always be a single request therefore no need for a loop here.

            var singleRequest = request.Request;

            using var smtpClient = new MailKit.Net.Smtp.SmtpClient();

            if (smtpClient.IsConnected && client.IsAuthenticated) return;

            if (!smtpClient.IsConnected)
                await smtpClient.ConnectAsync(Account.ServerInformation.OutgoingServer, int.Parse(Account.ServerInformation.OutgoingServerPort), MailKit.Security.SecureSocketOptions.Auto);

            if (!smtpClient.IsAuthenticated)
                await smtpClient.AuthenticateAsync(Account.ServerInformation.OutgoingServerUsername, Account.ServerInformation.OutgoingServerPassword);

            // Remove local draft header before sending to prevent leaking to recipients.
            singleRequest.Mime.Headers.Remove(Domain.Constants.WinoLocalDraftHeader);

            // TODO: Transfer progress implementation as popup in the UI.
            await smtpClient.SendAsync(singleRequest.Mime, default);
            await smtpClient.DisconnectAsync(true);

            // SMTP sent the message, but we need to remove it from the Draft folder.
            var draftFolder = singleRequest.MailItem.AssignedFolder;

            var folder = await client.GetFolderAsync(draftFolder.RemoteFolderId);

            await folder.OpenAsync(FolderAccess.ReadWrite);
            await folder.AddFlagsAsync(new UniqueId(MailkitClientExtensions.ResolveUid(singleRequest.MailItem.Id)), MessageFlags.Deleted, true);
            await folder.ExpungeAsync();
            await folder.CloseAsync();

            // Check whether we need to create a copy of the message to Sent folder.
            // This comes from the account preferences.

            if (singleRequest.AccountPreferences.ShouldAppendMessagesToSentFolder && singleRequest.SentFolder != null)
            {
                var sentFolder = await client.GetFolderAsync(singleRequest.SentFolder.RemoteFolderId);

                await sentFolder.OpenAsync(FolderAccess.ReadWrite);
                await sentFolder.AppendAsync(singleRequest.Mime, MessageFlags.Seen);
                await sentFolder.CloseAsync();
            }
        }, request, request);
    }

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem,
                                                           ITransferProgress transferProgress = null,
                                                           CancellationToken cancellationToken = default)
    {
        var folder = mailItem.AssignedFolder;
        var remoteFolderId = folder.RemoteFolderId;

        var client = await _clientPool.GetClientAsync().ConfigureAwait(false);

        try
        {
            var remoteFolder = await client.GetFolderAsync(remoteFolderId, cancellationToken).ConfigureAwait(false);

            var uniqueId = new UniqueId(MailkitClientExtensions.ResolveUid(mailItem.Id));

            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            var message = await remoteFolder.GetMessageAsync(uniqueId, cancellationToken, transferProgress).ConfigureAwait(false);

            await _imapChangeProcessor.SaveMimeFileAsync(mailItem.FileId, message, Account.Id).ConfigureAwait(false);
            await remoteFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (FolderNotFoundException ex)
        {
            _logger.Warning("IMAP folder {FolderId} not found during MIME download for {MailId}. Deleting locally.", remoteFolderId, mailItem.Id);
            await _imapChangeProcessor.DeleteMailAsync(Account.Id, mailItem.Id).ConfigureAwait(false);
            throw new SynchronizerEntityNotFoundException(ex.Message);
        }
        catch (ImapCommandException ex) when (ex.Response == ImapCommandResponse.No)
        {
            _logger.Warning("IMAP message {MailId} not found during MIME download (NO response). Deleting locally.", mailItem.Id);
            await _imapChangeProcessor.DeleteMailAsync(Account.Id, mailItem.Id).ConfigureAwait(false);
            throw new SynchronizerEntityNotFoundException(ex.Message);
        }
        finally
        {
            _clientPool.Release(client);
        }
    }

    public override Task DownloadCalendarAttachmentAsync(
        Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem,
        Wino.Core.Domain.Entities.Calendar.CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        // IMAP protocol doesn't support calendar operations natively
        // Calendar functionality would require CalDAV protocol
        _logger.Warning("IMAP protocol does not support calendar attachments. CalDAV would be required.");
        throw new NotSupportedException("IMAP does not support calendar attachments. Use Outlook or Gmail for calendar functionality.");
    }

    public override List<IRequestBundle<ImapRequest>> RenameFolder(RenameFolderRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            var folder = await client.GetFolderAsync(request.Folder.RemoteFolderId).ConfigureAwait(false);
            await folder.RenameAsync(folder.ParentFolder, request.NewFolderName).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<ImapRequest>> DeleteFolder(DeleteFolderRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            var folder = await client.GetFolderAsync(request.Folder.RemoteFolderId).ConfigureAwait(false);
            await folder.DeleteAsync().ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<ImapRequest>> CreateSubFolder(CreateSubFolderRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            var parentFolder = await client.GetFolderAsync(request.Folder.RemoteFolderId).ConfigureAwait(false);
            await parentFolder.CreateAsync(request.NewFolderName, true).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<ImapRequest>> CreateRootFolder(CreateRootFolderRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            var rootFolder = client.GetFolder(client.PersonalNamespaces[0]);
            await rootFolder.CreateAsync(request.NewFolderName, true).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<ImapRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var handler = ResolveCalendarOperationHandler();
        return CreateCalendarOperationTaskBundle(
            request,
            async value => await handler.CreateCalendarEventAsync(value).ConfigureAwait(false),
            handler.RequiresConnectedClient);
    }

    public override List<IRequestBundle<ImapRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var handler = ResolveCalendarOperationHandler();
        return CreateCalendarOperationTaskBundle(
            request,
            async value => await handler.UpdateCalendarEventAsync(value).ConfigureAwait(false),
            handler.RequiresConnectedClient);
    }

    public override List<IRequestBundle<ImapRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
    {
        var handler = ResolveCalendarOperationHandler();
        return CreateCalendarOperationTaskBundle(
            request,
            async value => await handler.UpdateCalendarEventAsync(value).ConfigureAwait(false),
            handler.RequiresConnectedClient);
    }

    public override List<IRequestBundle<ImapRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
    {
        var handler = ResolveCalendarOperationHandler();
        return CreateCalendarOperationTaskBundle(
            request,
            async value => await handler.DeleteCalendarEventAsync(value).ConfigureAwait(false),
            handler.RequiresConnectedClient);
    }

    public override List<IRequestBundle<ImapRequest>> AcceptEvent(AcceptEventRequest request)
    {
        var handler = ResolveCalendarOperationHandler();
        return CreateCalendarOperationTaskBundle(
            request,
            async value => await handler.AcceptEventAsync(value).ConfigureAwait(false),
            handler.RequiresConnectedClient);
    }

    public override List<IRequestBundle<ImapRequest>> DeclineEvent(DeclineEventRequest request)
    {
        var handler = ResolveCalendarOperationHandler();
        return CreateCalendarOperationTaskBundle(
            request,
            async value => await handler.DeclineEventAsync(value).ConfigureAwait(false),
            handler.RequiresConnectedClient);
    }

    public override List<IRequestBundle<ImapRequest>> TentativeEvent(TentativeEventRequest request)
    {
        var handler = ResolveCalendarOperationHandler();
        return CreateCalendarOperationTaskBundle(
            request,
            async value => await handler.TentativeEventAsync(value).ConfigureAwait(false),
            handler.RequiresConnectedClient);
    }

    private IImapCalendarOperationHandler ResolveCalendarOperationHandler()
    {
        var mode = Account.ServerInformation?.CalendarSupportMode ?? ImapCalendarSupportMode.Disabled;

        return mode switch
        {
            ImapCalendarSupportMode.LocalOnly => _localCalendarOperationHandler,
            ImapCalendarSupportMode.CalDav => _calDavCalendarOperationHandler,
            _ => throw new NotSupportedException("Calendar operations are disabled for this IMAP account.")
        };
    }

    private List<IRequestBundle<ImapRequest>> CreateCalendarOperationTaskBundle<TRequest>(
        TRequest request,
        Func<TRequest, Task> operation,
        bool requiresConnectedClient)
        where TRequest : IRequestBase, IUIChangeRequest
    {
        return
        [
            new ImapRequestBundle(
                new ImapRequest<TRequest>((client, value) => operation(value), request, requiresConnectedClient),
                request,
                request)
        ];
    }

    #endregion

    public override async Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(ImapMessageCreationPackage message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        var mailCopy = message.MessageSummary.GetMailDetails(assignedFolder, message.MimeMessage);

        // Draft folder message updates must be updated as IsDraft.
        // I couldn't find it in MimeMesssage...

        mailCopy.IsDraft = assignedFolder.SpecialFolderType == SpecialFolderType.Draft;

        // Check draft mapping.
        // This is the same implementation as in the OutlookSynchronizer.

        string draftHeaderValue = null;

        if (message.MimeMessage?.Headers?.Contains(Domain.Constants.WinoLocalDraftHeader) == true)
        {
            draftHeaderValue = message.MimeMessage.Headers[Domain.Constants.WinoLocalDraftHeader];
        }
        else if (message.MessageSummary?.Headers?.Contains(Domain.Constants.WinoLocalDraftHeader) == true)
        {
            draftHeaderValue = message.MessageSummary.Headers[Domain.Constants.WinoLocalDraftHeader];
        }

        if (Guid.TryParse(draftHeaderValue, out Guid localDraftCopyUniqueId))
        {
            // This message belongs to existing local draft copy.
            // We don't need to create a new mail copy for this message, just update the existing one.

            bool isMappingSuccessful = await _imapChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopyUniqueId, mailCopy.Id, draftHeaderValue, mailCopy.ThreadId);

            if (isMappingSuccessful) return null;

            // Local copy doesn't exists. Continue execution to insert mail copy.
        }

        var contacts = message.MimeMessage != null
            ? ExtractContactsFromMimeMessage(message.MimeMessage)
            : ExtractContactsFromMessageSummary(message.MessageSummary);

        var package = new NewMailItemPackage(mailCopy, message.MimeMessage, assignedFolder.RemoteFolderId, contacts);

        return
        [
            package
        ];
    }

    private static IReadOnlyList<AccountContact> ExtractContactsFromMimeMessage(MimeMessage mimeMessage)
    {
        if (mimeMessage == null) return [];

        var contacts = new Dictionary<string, AccountContact>(StringComparer.OrdinalIgnoreCase);

        AddFromInternetAddressList(mimeMessage.From);
        AddFromInternetAddressList(mimeMessage.To);
        AddFromInternetAddressList(mimeMessage.Cc);
        AddFromInternetAddressList(mimeMessage.Bcc);
        AddFromInternetAddressList(mimeMessage.ReplyTo);

        if (mimeMessage.Sender is MailboxAddress senderMailbox)
        {
            AddContact(senderMailbox.Address, senderMailbox.Name);
        }

        return contacts.Values.ToList();

        void AddFromInternetAddressList(InternetAddressList addresses)
        {
            if (addresses == null) return;

            foreach (var mailbox in addresses.Mailboxes)
            {
                AddContact(mailbox.Address, mailbox.Name);
            }
        }

        void AddContact(string address, string name)
        {
            var trimmedAddress = address?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedAddress)) return;

            var displayName = string.IsNullOrWhiteSpace(name) ? trimmedAddress : name.Trim();

            contacts[trimmedAddress] = new AccountContact
            {
                Address = trimmedAddress,
                Name = displayName
            };
        }
    }

    private static IReadOnlyList<AccountContact> ExtractContactsFromMessageSummary(IMessageSummary summary)
    {
        if (summary?.Envelope == null) return [];

        var contacts = new Dictionary<string, AccountContact>(StringComparer.OrdinalIgnoreCase);

        AddFromInternetAddressList(summary.Envelope.From);
        AddFromInternetAddressList(summary.Envelope.To);
        AddFromInternetAddressList(summary.Envelope.Cc);
        AddFromInternetAddressList(summary.Envelope.Bcc);
        AddFromInternetAddressList(summary.Envelope.ReplyTo);

        var senderMailbox = summary.Envelope.Sender?.Mailboxes?.FirstOrDefault();
        if (senderMailbox != null)
        {
            AddContact(senderMailbox.Address, senderMailbox.Name);
        }

        return contacts.Values.ToList();

        void AddFromInternetAddressList(InternetAddressList addresses)
        {
            if (addresses == null) return;

            foreach (var mailbox in addresses.Mailboxes)
            {
                AddContact(mailbox.Address, mailbox.Name);
            }
        }

        void AddContact(string address, string name)
        {
            var trimmedAddress = address?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedAddress)) return;

            var displayName = string.IsNullOrWhiteSpace(name) ? trimmedAddress : name.Trim();

            contacts[trimmedAddress] = new AccountContact
            {
                Address = trimmedAddress,
                Name = displayName
            };
        }
    }

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();
        var folderResults = new List<FolderSyncResult>();

        _logger.Information("Internal synchronization started for {Name}", Account.Name);
        _logger.Information("Options: {Options}", options);

        try
        {
            _isFolderStructureChanged = false;

            // Set indeterminate progress initially
            UpdateSyncProgress(0, 0, "Synchronizing...");

            bool shouldDoFolderSync = options.Type == MailSynchronizationType.FullFolders || options.Type == MailSynchronizationType.FoldersOnly;

            if (shouldDoFolderSync)
            {
                await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);

                if (_isFolderStructureChanged)
                {
                    WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
                }
            }

            if (options.Type != MailSynchronizationType.FoldersOnly)
            {
                var synchronizationFolders = await _imapChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);

                var totalFolders = synchronizationFolders.Count;
                const int maxParallelFolderSyncClients = 3;
                var folderSyncSemaphore = new SemaphoreSlim(maxParallelFolderSyncClients, maxParallelFolderSyncClients);
                using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var linkedToken = linkedCancellationTokenSource.Token;
                var resultLock = new object();
                int completedFolders = 0;

                var syncTasks = synchronizationFolders.Select(async folder =>
                {
                    await folderSyncSemaphore.WaitAsync(linkedToken).ConfigureAwait(false);

                    try
                    {
                        IImapClient client = null;

                        try
                        {
                            client = await _clientPool.GetClientAsync(linkedToken).ConfigureAwait(false);
                            var folderResult = await _unifiedSynchronizer
                                .SynchronizeFolderAsync(client, folder, this, Account.ServerInformation?.IncomingServer, linkedToken)
                                .ConfigureAwait(false);

                            List<string> folderDownloadedIds = null;
                            if (folderResult.Success && folderResult.DownloadedCount > 0)
                            {
                                folderDownloadedIds = await GetDownloadedIdsForFolderAsync(folder, folderResult.DownloadedCount).ConfigureAwait(false);
                            }

                            lock (resultLock)
                            {
                                folderResults.Add(folderResult);
                                if (folderDownloadedIds != null && folderDownloadedIds.Count > 0)
                                {
                                    downloadedMessageIds.AddRange(folderDownloadedIds);
                                }
                            }
                        }
                        finally
                        {
                            if (client != null)
                            {
                                _clientPool.Release(client);
                            }
                        }
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
                            FolderId = folder.Id,
                            FolderName = folder.FolderName,
                            OperationType = "ImapFolderSync"
                        };

                        _ = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                        var failedResult = FolderSyncResult.Failed(folder.Id, folder.FolderName, errorContext);

                        lock (resultLock)
                        {
                            folderResults.Add(failedResult);
                        }

                        if (!errorContext.CanContinueSync)
                        {
                            _logger.Error(ex, "Folder {FolderName} sync failed with fatal error", folder.FolderName);
                            linkedCancellationTokenSource.Cancel();
                            throw;
                        }

                        _logger.Warning(ex, "Folder {FolderName} sync failed, continuing with other folders", folder.FolderName);
                    }
                    finally
                    {
                        folderSyncSemaphore.Release();

                        var completed = Interlocked.Increment(ref completedFolders);
                        UpdateSyncProgress(totalFolders, totalFolders - completed, $"Syncing {folder.FolderName}...");
                    }
                }).ToList();

                await Task.WhenAll(syncTasks).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested) return MailSynchronizationResult.Canceled;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Synchronization was canceled for {Name}", Account.Name);
            return MailSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Synchronization failed for {Name}", Account.Name);
            return MailSynchronizationResult.Failed(ex);
        }
        finally
        {
            // Reset progress
            ResetSyncProgress();
        }

        // Get all unread new downloaded items and return in the result.
        // This is primarily used in notifications.

        var unreadNewItems = await _imapChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);

        return MailSynchronizationResult.CompletedWithFolderResults(unreadNewItems, folderResults);
    }

    /// <summary>
    /// Gets the most recent downloaded message IDs for a folder.
    /// Used for notification purposes after sync completes.
    /// </summary>
    private async Task<List<string>> GetDownloadedIdsForFolderAsync(MailItemFolder folder, int count)
    {
        // Get the most recent mail IDs from the folder
        var recentMails = await _imapChangeProcessor.GetRecentMailIdsForFolderAsync(folder.Id, count).ConfigureAwait(false);
        return recentMails?.ToList() ?? new List<string>();
    }

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<ImapRequest>> batchedRequests, CancellationToken cancellationToken = default)
    {
        // First apply the UI changes for each bundle.
        // This is important to reflect changes to the UI before the network call is done.

        ApplyOptimisticUiChanges(batchedRequests, ShouldApplyOptimisticUIChanges);

        // All task bundles will execute on the same client.
        // Tasks themselves don't pull the client from the pool
        // because exception handling is easier this way.
        // Also we might parallelize these bundles later on for additional performance.

        foreach (var item in batchedRequests)
        {
            // At this point this client is ready to execute async commands.
            // Each task bundle will await and execution will continue in case of error.

            IImapClient executorClient = null;

            bool isCrashed = false;

            try
            {
                if (item.NativeRequest.RequiresConnectedClient)
                {
                    executorClient = await _clientPool.GetClientAsync();
                }
            }
            catch (ImapClientPoolException)
            {
                // Client pool failed to get a client.
                // Requests may not be executed at this point.

                if (ShouldApplyOptimisticUIChanges(item.Request))
                {
                    item.UIChangeRequest?.RevertUIChanges();
                }

                isCrashed = true;
                throw;
            }
            finally
            {
                // Make sure that the client is released from the pool for next usages if error occurs.
                if (isCrashed && executorClient != null)
                {
                    _clientPool.Release(executorClient);
                }
            }

            try
            {
                await item.NativeRequest.IntegratorTask(executorClient, item.Request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errorContext = new SynchronizerErrorContext
                {
                    Account = Account,
                    ErrorCode = ex is FolderNotFoundException ? 404 : null,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    RequestBundle = item,
                    Request = item.Request,
                    OperationType = "RequestExecution",
                    IsEntityNotFound = ex is FolderNotFoundException || ex is SynchronizerEntityNotFoundException
                };

                var handled = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

                if (!handled)
                {
                    CaptureSynchronizationIssue(errorContext);

                    if (ShouldApplyOptimisticUIChanges(item.Request))
                    {
                        item.UIChangeRequest?.RevertUIChanges();
                    }
                    throw;
                }
            }
            finally
            {
                if (executorClient != null)
                {
                    _clientPool.Release(executorClient);
                }
            }
        }
    }

    private bool ShouldApplyOptimisticUIChanges(IRequestBase request)
    {
        // Mail changes are always applied.
        // Calendar changes are applied only if calendar is not in local mode.
        // Database updates are immidiate and will be reflected in the UI right after the request is processed, so no need for optimistic changes.

        if (request is not ICalendarActionRequest)
        {
            return true;
        }

        var mode = Account.ServerInformation?.CalendarSupportMode ?? ImapCalendarSupportMode.Disabled;
        return mode != ImapCalendarSupportMode.LocalOnly;
    }

    /// <summary>
    /// Assigns special folder type for the given local folder.
    /// If server doesn't support special folders, we can't determine the type. MailKit will throw for GetFolder.
    /// Default type is Other.
    /// </summary>
    /// <param name="executorClient">ImapClient from the pool</param>
    /// <param name="remoteFolder">Assigning remote folder.</param>
    /// <param name="localFolder">Assigning local folder.</param>
    private void AssignSpecialFolderType(IImapClient executorClient, IMailFolder remoteFolder, MailItemFolder localFolder)
    {
        // Inbox is awlawys available. Don't miss it for assignment even though XList or SpecialUser is not supported.
        if (executorClient.Inbox == remoteFolder)
        {
            localFolder.SpecialFolderType = SpecialFolderType.Inbox;
            return;

        }

        bool isSpecialFoldersSupported = executorClient.Capabilities.HasFlag(ImapCapabilities.SpecialUse) || executorClient.Capabilities.HasFlag(ImapCapabilities.XList);

        if (!isSpecialFoldersSupported)
        {
            localFolder.SpecialFolderType = SpecialFolderType.Other;
            return;
        }

        if (remoteFolder == executorClient.Inbox)
            localFolder.SpecialFolderType = SpecialFolderType.Inbox;
        else if (remoteFolder == executorClient.GetFolder(SpecialFolder.Drafts))
            localFolder.SpecialFolderType = SpecialFolderType.Draft;
        else if (remoteFolder == executorClient.GetFolder(SpecialFolder.Junk))
            localFolder.SpecialFolderType = SpecialFolderType.Junk;
        else if (remoteFolder == executorClient.GetFolder(SpecialFolder.Trash))
            localFolder.SpecialFolderType = SpecialFolderType.Deleted;
        else if (remoteFolder == executorClient.GetFolder(SpecialFolder.Sent))
            localFolder.SpecialFolderType = SpecialFolderType.Sent;
        else if (remoteFolder == executorClient.GetFolder(SpecialFolder.Archive))
            localFolder.SpecialFolderType = SpecialFolderType.Archive;
        else if (remoteFolder == executorClient.GetFolder(SpecialFolder.Important))
            localFolder.SpecialFolderType = SpecialFolderType.Important;
        else if (remoteFolder == executorClient.GetFolder(SpecialFolder.Flagged))
            localFolder.SpecialFolderType = SpecialFolderType.Starred;
    }

    private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken = default)
    {
        // https://www.rfc-editor.org/rfc/rfc4549#section-1.1

        var localFolders = await _imapChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);

        IImapClient executorClient = null;

        try
        {
            List<MailItemFolder> insertedFolders = new();
            List<MailItemFolder> updatedFolders = new();
            List<MailItemFolder> deletedFolders = new();

            executorClient = await _clientPool.GetClientAsync().ConfigureAwait(false);

            var remoteFolders = (await executorClient.GetFoldersAsync(executorClient.PersonalNamespaces[0], cancellationToken: cancellationToken)).ToList();

            // 1. First check deleted folders.

            // 1.a If local folder doesn't exists remotely, delete it.
            // 1.b If local folder exists remotely, check if it is still a valid folder. If UidValidity is changed, delete it.

            foreach (var localFolder in localFolders)
            {
                IMailFolder remoteFolder = null;

                try
                {
                    remoteFolder = remoteFolders.FirstOrDefault(a => a.FullName == localFolder.RemoteFolderId);

                    bool shouldDeleteLocalFolder = false;

                    // Check UidValidity of the remote folder if exists.

                    if (remoteFolder != null)
                    {
                        // UidValidity won't be available until it's opened.
                        await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

                        shouldDeleteLocalFolder = remoteFolder.UidValidity != localFolder.UidValidity;
                    }
                    else
                    {
                        // Remote folder doesn't exist. Delete it.
                        shouldDeleteLocalFolder = true;
                    }

                    if (shouldDeleteLocalFolder)
                    {
                        await _imapChangeProcessor.DeleteFolderAsync(Account.Id, localFolder.RemoteFolderId).ConfigureAwait(false);

                        deletedFolders.Add(localFolder);
                    }
                }
                catch (Exception)
                {
                    throw;
                }
                finally
                {
                    if (remoteFolder != null)
                    {
                        await remoteFolder.CloseAsync().ConfigureAwait(false);
                    }
                }
            }

            deletedFolders.ForEach(a => localFolders.Remove(a));

            // 2. Get all remote folders and insert/update each of them.

            var nameSpace = executorClient.PersonalNamespaces[0];

            IMailFolder inbox = executorClient.Inbox;

            // Sometimes Inbox is the root namespace. We need to check for that.
            if (inbox != null && !remoteFolders.Contains(inbox))
                remoteFolders.Add(inbox);

            foreach (var remoteFolder in remoteFolders)
            {
                // Namespaces are not needed as folders.
                // Non-existed folders don't need to be synchronized.

                if (remoteFolder.IsNamespace && !remoteFolder.Attributes.HasFlag(FolderAttributes.Inbox) || !remoteFolder.Exists)
                    continue;

                // Ignore folders that can't be opened.
                if (!remoteFolder.CanOpen) continue;

                var existingLocalFolder = localFolders.FirstOrDefault(a => a.RemoteFolderId == remoteFolder.FullName);

                if (existingLocalFolder == null)
                {
                    // Folder doesn't exist locally. Insert it.

                    var localFolder = remoteFolder.GetLocalFolder();

                    // Check whether this is a special folder.
                    AssignSpecialFolderType(executorClient, remoteFolder, localFolder);

                    bool isSystemFolder = localFolder.SpecialFolderType != SpecialFolderType.Other;

                    localFolder.IsSynchronizationEnabled = isSystemFolder;
                    localFolder.IsSticky = isSystemFolder;

                    // By default, all special folders update unread count in the UI except Trash.
                    localFolder.ShowUnreadCount = localFolder.SpecialFolderType != SpecialFolderType.Deleted || localFolder.SpecialFolderType != SpecialFolderType.Other;

                    localFolder.MailAccountId = Account.Id;

                    // Sometimes sub folders are parented under Inbox.
                    // Even though this makes sense in server level, in the client it sucks.
                    // That will make sub folders to be parented under Inbox in the client.
                    // Instead, we will mark them as non-parented folders.
                    // This is better. Model allows personalized folder structure anyways
                    // even though we don't have the page/control to adjust it.

                    if (remoteFolder.ParentFolder == executorClient.Inbox)
                        localFolder.ParentRemoteFolderId = string.Empty;

                    // Set UidValidity for cache expiration.
                    // Folder must be opened for this.

                    await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                    localFolder.UidValidity = remoteFolder.UidValidity;

                    await remoteFolder.CloseAsync(cancellationToken: cancellationToken);

                    insertedFolders.Add(localFolder);
                }
                else
                {
                    // Update existing folder. Right now we only update the name.

                    // TODO: Moving folders around different parents. This is not supported right now.
                    // We will need more comphrensive folder update mechanism to support this.

                    if (ShouldUpdateFolder(remoteFolder, existingLocalFolder))
                    {
                        existingLocalFolder.FolderName = remoteFolder.Name;
                        updatedFolders.Add(existingLocalFolder);
                    }
                    else
                    {
                        // Remove it from the local folder list to skip additional folder updates.
                        localFolders.Remove(existingLocalFolder);
                    }
                }
            }

            // Process changes in order-> Insert, Update. Deleted ones are already processed.

            foreach (var folder in insertedFolders)
            {
                await _imapChangeProcessor.InsertFolderAsync(folder).ConfigureAwait(false);
            }

            foreach (var folder in updatedFolders)
            {
                await _imapChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
            }

            if (insertedFolders.Any() || deletedFolders.Any() || updatedFolders.Any())
            {
                _isFolderStructureChanged = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Synchronizing IMAP folders failed.");

            throw;
        }
        finally
        {
            if (executorClient != null)
            {
                _clientPool.Release(executorClient);
            }
        }
    }

    public override async Task<List<MailCopy>> OnlineSearchAsync(string queryText, List<IMailItemFolder> folders, CancellationToken cancellationToken = default)
    {
        IImapClient client = null;

        try
        {
            client = await _clientPool.GetClientAsync().ConfigureAwait(false);

            var distinctFolders = folders?
                .Where(folder => folder != null)
                .GroupBy(folder => folder.Id)
                .Select(group => group.First())
                .ToList() ?? [];

            HashSet<string> searchResultFolderMailUids = new(StringComparer.Ordinal);

            foreach (var folder in distinctFolders)
            {
                if (folder is not MailItemFolder localFolder)
                    continue;

                var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
                await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

                // Look for subject and body.
                var query = SearchQuery.BodyContains(queryText).Or(SearchQuery.SubjectContains(queryText));

                var searchResultsInFolder = await remoteFolder.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                Dictionary<string, UniqueId> searchResultsIdsInFolder = [];

                foreach (var searchResultId in searchResultsInFolder)
                {
                    var folderMailUid = MailkitClientExtensions.CreateUid(folder.Id, searchResultId.Id);
                    searchResultFolderMailUids.Add(folderMailUid);
                    searchResultsIdsInFolder.Add(folderMailUid, searchResultId);
                }

                // Populate no foundIds
                var foundIds = await _imapChangeProcessor.AreMailsExistsAsync(searchResultsIdsInFolder.Select(a => a.Key));
                var notFoundIds = searchResultsIdsInFolder.Keys.Except(foundIds);

                List<UniqueId> nonExistingUniqueIds = [];
                foreach (var nonExistingId in notFoundIds)
                {
                    nonExistingUniqueIds.Add(searchResultsIdsInFolder[nonExistingId]);
                }

                if (nonExistingUniqueIds.Count != 0)
                {
                    await _unifiedSynchronizer
                        .DownloadMessagesByUidsAsync(client, remoteFolder, localFolder, nonExistingUniqueIds, this, cancellationToken)
                        .ConfigureAwait(false);
                }

                await remoteFolder.CloseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await _imapChangeProcessor.GetMailCopiesAsync(searchResultFolderMailUids);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to perform online imap search.");
            throw;
        }
        finally
        {
            _clientPool.Release(client);
        }
    }

    /// <summary>
    /// Whether the local folder should be updated with the remote folder.
    /// IMAP only compares folder name for now.
    /// </summary>
    /// <param name="remoteFolder">Remote folder</param>
    /// <param name="localFolder">Local folder.</param>
    public bool ShouldUpdateFolder(IMailFolder remoteFolder, MailItemFolder localFolder)
        => !localFolder.FolderName.Equals(remoteFolder.Name, StringComparison.OrdinalIgnoreCase);

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.ProviderType != MailProviderType.IMAP4 || !Account.IsCalendarAccessGranted || Account.ServerInformation == null)
            return CalendarSynchronizationResult.Empty;

        if (Account.ServerInformation.CalendarSupportMode is ImapCalendarSupportMode.Disabled or ImapCalendarSupportMode.LocalOnly)
            return CalendarSynchronizationResult.Empty;

        var calDavServiceUri = await ResolveCalDavServiceUriAsync(cancellationToken).ConfigureAwait(false);
        if (calDavServiceUri == null)
        {
            _logger.Information("Skipping calendar sync for {AccountName}: CalDAV endpoint is not configured.", Account.Name);
            return CalendarSynchronizationResult.Empty;
        }

        var password = ResolveCalDavPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            _logger.Warning("Skipping calendar sync for {AccountName}: empty credentials.", Account.Name);
            return CalendarSynchronizationResult.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var calDavUsername = ResolveCalDavUsername();
        if (string.IsNullOrWhiteSpace(calDavUsername))
        {
            _logger.Warning("Skipping calendar sync for {AccountName}: account email address is empty for CalDAV credentials.", Account.Name);
            return CalendarSynchronizationResult.Empty;
        }

        var activeConnection = new CalDavConnectionSettings
        {
            ServiceUri = calDavServiceUri,
            Username = calDavUsername,
            Password = password
        };

        IReadOnlyList<CalDavCalendar> remoteCalendars;

        try
        {
            remoteCalendars = await _calDavClient
                .DiscoverCalendarsAsync(activeConnection, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.Warning("Skipping calendar sync for {AccountName}: CalDAV authentication failed for username {Username}.", Account.Name, calDavUsername);
            return CalendarSynchronizationResult.Empty;
        }

        await SynchronizeCalendarMetadataAsync(remoteCalendars).ConfigureAwait(false);

        if (options?.Type == CalendarSynchronizationType.CalendarMetadata)
            return CalendarSynchronizationResult.Empty;

        var localCalendars = await _imapChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        var remoteCalendarsById = remoteCalendars.ToDictionary(c => c.RemoteCalendarId, StringComparer.OrdinalIgnoreCase);

        if (options?.Type == CalendarSynchronizationType.SingleCalendar && options.SynchronizationCalendarIds?.Count > 0)
        {
            localCalendars = localCalendars
                .Where(c => options.SynchronizationCalendarIds.Contains(c.Id))
                .ToList();
        }

        localCalendars = localCalendars
            .Where(c => c.IsSynchronizationEnabled)
            .ToList();

        var periodStartUtc = DateTimeOffset.UtcNow.AddYears(-1);
        var periodEndUtc = DateTimeOffset.UtcNow.AddYears(2);

        var totalCalendars = localCalendars.Count;
        if (totalCalendars > 0)
        {
            UpdateSyncProgress(totalCalendars, totalCalendars, Translator.SyncAction_SynchronizingCalendarEvents);
        }

        for (int i = 0; i < totalCalendars; i++)
        {
            var localCalendar = localCalendars[i];

            cancellationToken.ThrowIfCancellationRequested();

            if (!remoteCalendarsById.TryGetValue(localCalendar.RemoteCalendarId, out var remoteCalendar))
                continue;

            var remoteToken = BuildCalendarDeltaToken(remoteCalendar);

            var isInitialSync = string.IsNullOrWhiteSpace(localCalendar.SynchronizationDeltaToken);
            var tokenChanged = !string.Equals(localCalendar.SynchronizationDeltaToken, remoteToken, StringComparison.Ordinal);
            var forceSync = options?.Type is CalendarSynchronizationType.ExecuteRequests or CalendarSynchronizationType.SingleCalendar;

            if (!isInitialSync && !tokenChanged && !forceSync)
                continue;

            var remoteEvents = await _calDavClient.GetCalendarEventsAsync(
                activeConnection,
                remoteCalendar,
                periodStartUtc,
                periodEndUtc,
                cancellationToken).ConfigureAwait(false);
            var remoteEventIds = new HashSet<string>(
                remoteEvents
                    .Where(e => !string.IsNullOrWhiteSpace(e.RemoteEventId))
                    .Select(e => e.RemoteEventId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var remoteEvent in remoteEvents)
            {
                var existingLocalItem = await _imapChangeProcessor
                    .GetCalendarItemAsync(localCalendar.Id, remoteEvent.RemoteEventId)
                    .ConfigureAwait(false);

                var shouldSkipUnchangedEvent = await ShouldSkipUnchangedCalDavEventAsync(
                    localCalendar,
                    existingLocalItem,
                    remoteEvent).ConfigureAwait(false);

                if (shouldSkipUnchangedEvent)
                    continue;

                await _imapChangeProcessor
                    .ManageCalendarEventAsync(remoteEvent, localCalendar, Account)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(remoteEvent.IcsContent))
                    continue;

                var localItem = existingLocalItem ?? await _imapChangeProcessor
                    .GetCalendarItemAsync(localCalendar.Id, remoteEvent.RemoteEventId)
                    .ConfigureAwait(false);

                if (localItem == null)
                    continue;

                await _imapChangeProcessor
                    .SaveCalendarItemIcsAsync(
                        Account.Id,
                        localCalendar.Id,
                        localItem.Id,
                        remoteEvent.RemoteEventId,
                        remoteEvent.RemoteResourceHref,
                        remoteEvent.ETag,
                        remoteEvent.IcsContent)
                    .ConfigureAwait(false);
            }

            await ReconcileDeletedCalendarItemsAsync(localCalendar, periodStartUtc, periodEndUtc, remoteEventIds)
                .ConfigureAwait(false);

            localCalendar.SynchronizationDeltaToken = remoteToken;
            await _imapChangeProcessor.UpdateAccountCalendarAsync(localCalendar).ConfigureAwait(false);
            UpdateSyncProgress(totalCalendars, totalCalendars - (i + 1), Translator.SyncAction_SynchronizingCalendarEvents);
        }

        return CalendarSynchronizationResult.Empty;
    }

    private async Task<bool> ShouldSkipUnchangedCalDavEventAsync(
        AccountCalendar localCalendar,
        CalendarItem existingLocalItem,
        CalDavCalendarEvent remoteEvent)
    {
        if (localCalendar == null || existingLocalItem == null || remoteEvent == null)
            return false;

        // Ensure unresolved parent-child linkage still gets corrected when required.
        if (!string.IsNullOrWhiteSpace(remoteEvent.SeriesMasterRemoteEventId) &&
            existingLocalItem.RecurringCalendarItemId == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(remoteEvent.ETag))
            return false;

        var savedETag = await _imapChangeProcessor
            .GetCalendarItemIcsETagAsync(Account.Id, localCalendar.Id, existingLocalItem.Id)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(savedETag))
            return false;

        return string.Equals(savedETag.Trim(), remoteEvent.ETag.Trim(), StringComparison.Ordinal);
    }

    private async Task ReconcileDeletedCalendarItemsAsync(
        AccountCalendar localCalendar,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        HashSet<string> remoteEventIds)
    {
        var syncPeriod = new TimeRange(periodStartUtc.UtcDateTime, periodEndUtc.UtcDateTime);
        var localEventsInWindow = await _calendarService
            .GetCalendarEventsAsync(localCalendar, syncPeriod)
            .ConfigureAwait(false);

        foreach (var localEvent in localEventsInWindow)
        {
            if (string.IsNullOrWhiteSpace(localEvent.RemoteEventId))
                continue;

            if (remoteEventIds.Contains(localEvent.RemoteEventId))
                continue;

            await _imapChangeProcessor.DeleteCalendarItemAsync(localEvent.Id).ConfigureAwait(false);
        }
    }

    private static string BuildCalendarDeltaToken(CalDavCalendar calendar)
    {
        if (calendar == null)
            return string.Empty;

        var syncToken = calendar.SyncToken?.Trim() ?? string.Empty;
        var ctag = calendar.CTag?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(syncToken) && !string.IsNullOrWhiteSpace(ctag))
            return $"{syncToken}|{ctag}";

        return !string.IsNullOrWhiteSpace(syncToken) ? syncToken : ctag;
    }

    private async Task<Uri> ResolveCalDavServiceUriAsync(CancellationToken cancellationToken)
    {
        var explicitCalDavUri = TryGetExplicitCalDavServiceUri();
        if (explicitCalDavUri != null)
        {
            _cachedCalDavServiceUri = explicitCalDavUri;
            _isCalDavDiscoveryAttempted = true;
            return _cachedCalDavServiceUri;
        }

        if (_cachedCalDavServiceUri != null)
            return _cachedCalDavServiceUri;

        if (_isCalDavDiscoveryAttempted)
            return null;

        await _calDavDiscoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_cachedCalDavServiceUri != null)
                return _cachedCalDavServiceUri;

            if (_isCalDavDiscoveryAttempted)
                return null;

            _isCalDavDiscoveryAttempted = true;

            var emailCandidates = new[]
            {
                Account.ServerInformation?.Address,
                Account.Address
            }
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            foreach (var email in emailCandidates)
            {
                var discoveredUri = await _autoDiscoveryService
                    .DiscoverCalDavServiceUriAsync(email, cancellationToken)
                    .ConfigureAwait(false);

                if (discoveredUri == null)
                    continue;

                _cachedCalDavServiceUri = discoveredUri;
                return _cachedCalDavServiceUri;
            }

            if (Account.SpecialImapProvider == SpecialImapProvider.iCloud)
            {
                _cachedCalDavServiceUri = new Uri("https://caldav.icloud.com/");
                return _cachedCalDavServiceUri;
            }

            if (Account.SpecialImapProvider == SpecialImapProvider.Yahoo)
            {
                _cachedCalDavServiceUri = new Uri("https://caldav.calendar.yahoo.com/");
                return _cachedCalDavServiceUri;
            }

            return null;
        }
        finally
        {
            _calDavDiscoveryLock.Release();
        }
    }

    private string ResolveCalDavPassword()
    {
        if (!string.IsNullOrWhiteSpace(Account.ServerInformation?.CalDavPassword))
            return Account.ServerInformation.CalDavPassword;

        if (!string.IsNullOrWhiteSpace(Account.ServerInformation?.IncomingServerPassword))
            return Account.ServerInformation.IncomingServerPassword;

        if (!string.IsNullOrWhiteSpace(Account.ServerInformation?.OutgoingServerPassword))
            return Account.ServerInformation.OutgoingServerPassword;

        return string.Empty;
    }

    private string ResolveCalDavUsername()
    {
        if (!string.IsNullOrWhiteSpace(Account.ServerInformation?.CalDavUsername))
            return Account.ServerInformation.CalDavUsername.Trim();

        if (!string.IsNullOrWhiteSpace(Account.ServerInformation?.Address))
            return Account.ServerInformation.Address.Trim();

        if (!string.IsNullOrWhiteSpace(Account.Address))
            return Account.Address.Trim();

        return string.Empty;
    }

    private Uri TryGetExplicitCalDavServiceUri()
    {
        var configuredUrl = Account.ServerInformation?.CalDavServiceUrl;
        if (string.IsNullOrWhiteSpace(configuredUrl))
            return null;

        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri))
        {
            _logger.Warning("Configured CalDAV URL is invalid for account {AccountName}: {Url}", Account.Name, configuredUrl);
            return null;
        }

        return uri;
    }

    private async Task SynchronizeCalendarMetadataAsync(IReadOnlyList<CalDavCalendar> remoteCalendars)
    {
        var localCalendars = await _imapChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        var remoteCalendarsById = remoteCalendars
            .GroupBy(c => c.RemoteCalendarId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var usedCalendarColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var remotePrimaryCalendarId = GetPrimaryCalDavCalendarId(remoteCalendars);

        foreach (var localCalendar in localCalendars.ToList())
        {
            if (remoteCalendarsById.ContainsKey(localCalendar.RemoteCalendarId))
                continue;

            await _imapChangeProcessor
                .DeleteCalendarIcsForCalendarAsync(Account.Id, localCalendar.Id)
                .ConfigureAwait(false);
            await _imapChangeProcessor.DeleteAccountCalendarAsync(localCalendar).ConfigureAwait(false);
            localCalendars.Remove(localCalendar);
        }

        foreach (var remoteCalendar in remoteCalendars)
        {
            var existingLocal = localCalendars.FirstOrDefault(c =>
                string.Equals(c.RemoteCalendarId, remoteCalendar.RemoteCalendarId, StringComparison.OrdinalIgnoreCase));

            var isPrimary = string.Equals(remoteCalendar.RemoteCalendarId, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);

            if (existingLocal == null)
            {
                var insertedCalendarColor = ResolveSynchronizedCalendarBackgroundColor(remoteCalendar.BackgroundColorHex, null, usedCalendarColors);
                var newCalendar = new AccountCalendar
                {
                    Id = Guid.NewGuid(),
                    AccountId = Account.Id,
                    RemoteCalendarId = remoteCalendar.RemoteCalendarId,
                    Name = remoteCalendar.Name,
                    IsPrimary = isPrimary,
                    IsReadOnly = remoteCalendar.IsReadOnly,
                    IsSynchronizationEnabled = true,
                    IsExtended = true,
                    DefaultShowAs = remoteCalendar.DefaultShowAs,
                    BackgroundColorHex = insertedCalendarColor,
                    TimeZone = remoteCalendar.TimeZone,
                    SynchronizationDeltaToken = string.Empty
                };

                newCalendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(newCalendar.BackgroundColorHex);
                usedCalendarColors.Add(newCalendar.BackgroundColorHex);
                await _imapChangeProcessor.InsertAccountCalendarAsync(newCalendar).ConfigureAwait(false);
                continue;
            }

            var resolvedColor = ResolveSynchronizedCalendarBackgroundColor(remoteCalendar.BackgroundColorHex, existingLocal, usedCalendarColors);
            var resolvedTextColor = ColorHelpers.GetReadableTextColorHex(resolvedColor);
            var shouldUpdate = !string.Equals(existingLocal.Name, remoteCalendar.Name, StringComparison.Ordinal)
                               || !string.Equals(existingLocal.TimeZone, remoteCalendar.TimeZone, StringComparison.OrdinalIgnoreCase)
                               || existingLocal.IsReadOnly != remoteCalendar.IsReadOnly
                               || existingLocal.DefaultShowAs != remoteCalendar.DefaultShowAs
                               || existingLocal.IsPrimary != isPrimary
                               || !string.Equals(existingLocal.BackgroundColorHex, resolvedColor, StringComparison.OrdinalIgnoreCase)
                               || !string.Equals(existingLocal.TextColorHex, resolvedTextColor, StringComparison.OrdinalIgnoreCase);

            if (!shouldUpdate)
            {
                usedCalendarColors.Add(resolvedColor);
                continue;
            }

            existingLocal.Name = remoteCalendar.Name;
            existingLocal.TimeZone = remoteCalendar.TimeZone;
            existingLocal.IsReadOnly = remoteCalendar.IsReadOnly;
            existingLocal.DefaultShowAs = remoteCalendar.DefaultShowAs;
            existingLocal.IsPrimary = isPrimary;
            existingLocal.BackgroundColorHex = resolvedColor;
            existingLocal.TextColorHex = resolvedTextColor;
            usedCalendarColors.Add(existingLocal.BackgroundColorHex);
            await _imapChangeProcessor.UpdateAccountCalendarAsync(existingLocal).ConfigureAwait(false);
        }
    }

    private static string GetPrimaryCalDavCalendarId(IReadOnlyList<CalDavCalendar> remoteCalendars)
    {
        if (remoteCalendars == null || remoteCalendars.Count == 0)
            return string.Empty;

        if (remoteCalendars.Any(calendar => calendar.Order.HasValue))
        {
            return remoteCalendars
                .OrderBy(calendar => calendar.Order ?? double.MaxValue)
                .ThenBy(calendar => calendar.Name, StringComparer.OrdinalIgnoreCase)
                .Select(calendar => calendar.RemoteCalendarId)
                .FirstOrDefault() ?? string.Empty;
        }

        return remoteCalendars.First().RemoteCalendarId;
    }

    private static string ResolveSynchronizedCalendarBackgroundColor(
        string remoteBackgroundColor,
        AccountCalendar accountCalendar,
        ISet<string> usedCalendarColors = null)
    {
        if (accountCalendar?.IsBackgroundColorUserOverridden == true)
            return accountCalendar.BackgroundColorHex;

        var preferredColor = string.IsNullOrWhiteSpace(remoteBackgroundColor)
            ? accountCalendar?.BackgroundColorHex
            : remoteBackgroundColor;

        if (string.IsNullOrWhiteSpace(remoteBackgroundColor) && usedCalendarColors != null)
            return ColorHelpers.GetDistinctFlatColorHex(usedCalendarColors, preferredColor);

        return string.IsNullOrWhiteSpace(preferredColor)
            ? ColorHelpers.GenerateFlatColorHex()
            : preferredColor;
    }

    private interface IImapCalendarOperationHandler
    {
        bool RequiresConnectedClient { get; }
        Task CreateCalendarEventAsync(CreateCalendarEventRequest request);
        Task UpdateCalendarEventAsync(UpdateCalendarEventRequest request);
        Task DeleteCalendarEventAsync(DeleteCalendarEventRequest request);
        Task AcceptEventAsync(AcceptEventRequest request);
        Task DeclineEventAsync(DeclineEventRequest request);
        Task TentativeEventAsync(TentativeEventRequest request);
    }

    private class LocalCalendarOperationHandler : IImapCalendarOperationHandler
    {
        private readonly MailAccount _account;
        private readonly IImapChangeProcessor _changeProcessor;
        private readonly ICalendarService _calendarService;
        private readonly string _applicationDataFolderPath;
        private readonly string _resourceScheme;

        public bool RequiresConnectedClient => false;

        public LocalCalendarOperationHandler(MailAccount account, IImapChangeProcessor changeProcessor, ICalendarService calendarService, string applicationDataFolderPath, string resourceScheme)
        {
            _account = account;
            _changeProcessor = changeProcessor;
            _calendarService = calendarService;
            _applicationDataFolderPath = applicationDataFolderPath;
            _resourceScheme = resourceScheme;
        }

        public async Task CreateCalendarEventAsync(CreateCalendarEventRequest request)
        {
            var item = request.PreparedItem;
            var attendees = request.PreparedEvent.Attendees;
            var reminders = request.PreparedEvent.Reminders;
            EnsureCalendarItemDefaults(item, _account, "local");
            item.AssignedCalendar ??= await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);

            var existing = await _calendarService.GetCalendarItemAsync(item.Id).ConfigureAwait(false);

            if (existing == null)
                await _calendarService.CreateNewCalendarItemAsync(item, attendees).ConfigureAwait(false);
            else
                await _calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);

            await _calendarService.SaveRemindersAsync(item.Id, reminders).ConfigureAwait(false);
            await SaveAttachmentsAsync(request.ComposeResult, item.Id).ConfigureAwait(false);
            await PersistIcsAsync(item, attendees).ConfigureAwait(false);
        }

        public async Task UpdateCalendarEventAsync(UpdateCalendarEventRequest request)
        {
            var item = request.Item;
            EnsureCalendarItemDefaults(item, _account, "local");
            item.AssignedCalendar ??= await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);

            var attendees = request.Attendees ?? await _calendarService.GetAttendeesAsync(item.Id).ConfigureAwait(false);

            await _calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
            await PersistIcsAsync(item, attendees).ConfigureAwait(false);
        }

        public Task DeleteCalendarEventAsync(DeleteCalendarEventRequest request)
            => _changeProcessor.DeleteCalendarItemAsync(request.Item.Id);

        public async Task AcceptEventAsync(AcceptEventRequest request)
        {
            request.Item.Status = CalendarItemStatus.Accepted;
            await UpdateStatusAsync(request.Item).ConfigureAwait(false);
        }

        public async Task DeclineEventAsync(DeclineEventRequest request)
        {
            request.Item.Status = CalendarItemStatus.Cancelled;
            await UpdateStatusAsync(request.Item).ConfigureAwait(false);
        }

        public async Task TentativeEventAsync(TentativeEventRequest request)
        {
            request.Item.Status = CalendarItemStatus.Tentative;
            await UpdateStatusAsync(request.Item).ConfigureAwait(false);
        }

        private async Task UpdateStatusAsync(CalendarItem item)
        {
            EnsureCalendarItemDefaults(item, _account, "local");
            item.AssignedCalendar ??= await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);

            var attendees = await _calendarService.GetAttendeesAsync(item.Id).ConfigureAwait(false);
            await _calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
            await PersistIcsAsync(item, attendees).ConfigureAwait(false);
        }

        private Task PersistIcsAsync(CalendarItem item, List<CalendarEventAttendee> attendees)
        {
            var resourceHref = $"{_resourceScheme}://calendar/{item.CalendarId:N}/{item.Id:N}";
            var icsContent = BuildIcsContent(item, attendees);

            return _changeProcessor.SaveCalendarItemIcsAsync(
                _account.Id,
                item.CalendarId,
                item.Id,
                item.RemoteEventId,
                resourceHref,
                DateTimeOffset.UtcNow.ToString("O"),
                icsContent);
        }

        private async Task SaveAttachmentsAsync(CalendarEventComposeResult composeResult, Guid calendarItemId)
        {
            await _calendarService.DeleteAttachmentsAsync(calendarItemId).ConfigureAwait(false);

            var attachments = composeResult?.Attachments;
            if (attachments == null || attachments.Count == 0)
                return;

            var attachmentsRoot = Path.Combine(_applicationDataFolderPath, "CalendarAttachments", calendarItemId.ToString("N"));
            Directory.CreateDirectory(attachmentsRoot);

            var storedAttachments = new List<CalendarAttachment>();

            foreach (var attachment in attachments.Where(a => !string.IsNullOrWhiteSpace(a.FilePath) && File.Exists(a.FilePath)))
            {
                var fileName = string.IsNullOrWhiteSpace(attachment.FileName) ? Path.GetFileName(attachment.FilePath) : attachment.FileName;
                var destinationPath = Path.Combine(attachmentsRoot, fileName);
                File.Copy(attachment.FilePath, destinationPath, overwrite: true);

                storedAttachments.Add(new CalendarAttachment
                {
                    Id = Guid.NewGuid(),
                    CalendarItemId = calendarItemId,
                    RemoteAttachmentId = attachment.Id.ToString("N"),
                    FileName = fileName,
                    Size = attachment.Size,
                    ContentType = MimeTypes.GetMimeType(fileName),
                    IsDownloaded = true,
                    LocalFilePath = destinationPath,
                    LastModified = DateTimeOffset.UtcNow
                });
            }

            if (storedAttachments.Count > 0)
            {
                await _calendarService.InsertOrReplaceAttachmentsAsync(storedAttachments).ConfigureAwait(false);
            }
        }
    }

    private sealed class CalDavCalendarOperationHandler : IImapCalendarOperationHandler
    {
        private readonly ImapSynchronizer _owner;
        private readonly MailAccount _account;
        private readonly ICalendarService _calendarService;
        private readonly ICalDavClient _calDavClient;

        public bool RequiresConnectedClient => false;

        public CalDavCalendarOperationHandler(
            ImapSynchronizer owner,
            MailAccount account,
            ICalendarService calendarService,
            ICalDavClient calDavClient)
        {
            _owner = owner;
            _account = account;
            _calendarService = calendarService;
            _calDavClient = calDavClient;
        }

        public Task CreateCalendarEventAsync(CreateCalendarEventRequest request)
            => UpsertCalendarEventAsync(request.PreparedItem, request.PreparedEvent.Attendees);

        public Task UpdateCalendarEventAsync(UpdateCalendarEventRequest request)
            => UpsertCalendarEventAsync(request.Item, request.Attendees);

        public async Task DeleteCalendarEventAsync(DeleteCalendarEventRequest request)
        {
            var (connection, calendar) = await ResolveCalDavContextAsync(request.Item.CalendarId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(request.Item?.RemoteEventId))
            {
                throw new InvalidOperationException("Cannot delete CalDAV event because remote event ID is missing.");
            }

            await _calDavClient
                .DeleteCalendarEventAsync(connection, calendar, request.Item.RemoteEventId.GetProviderRemoteEventId())
                .ConfigureAwait(false);
        }

        public Task AcceptEventAsync(AcceptEventRequest request)
        {
            request.Item.Status = CalendarItemStatus.Accepted;
            return UpsertCalendarEventAsync(request.Item, null);
        }

        public Task DeclineEventAsync(DeclineEventRequest request)
        {
            request.Item.Status = CalendarItemStatus.Cancelled;
            return UpsertCalendarEventAsync(request.Item, null);
        }

        public Task TentativeEventAsync(TentativeEventRequest request)
        {
            request.Item.Status = CalendarItemStatus.Tentative;
            return UpsertCalendarEventAsync(request.Item, null);
        }

        private async Task UpsertCalendarEventAsync(CalendarItem item, List<CalendarEventAttendee> attendees)
        {
            EnsureCalendarItemDefaults(item, _account, "caldav");

            if (attendees == null)
            {
                attendees = await _calendarService.GetAttendeesAsync(item.Id).ConfigureAwait(false);
            }

            var (connection, calendar) = await ResolveCalDavContextAsync(item.CalendarId).ConfigureAwait(false);
            var icsContent = BuildIcsContent(item, attendees);

            await _calDavClient
                .UpsertCalendarEventAsync(connection, calendar, item.RemoteEventId.GetProviderRemoteEventId(), icsContent)
                .ConfigureAwait(false);
        }

        private async Task<(CalDavConnectionSettings Connection, CalDavCalendar Calendar)> ResolveCalDavContextAsync(Guid calendarId)
        {
            var assignedCalendar = await _calendarService.GetAccountCalendarAsync(calendarId).ConfigureAwait(false);
            if (assignedCalendar == null || string.IsNullOrWhiteSpace(assignedCalendar.RemoteCalendarId))
            {
                throw new InvalidOperationException("Cannot execute CalDAV operation because the target calendar has no remote ID.");
            }

            var serviceUri = await _owner.ResolveCalDavServiceUriAsync(CancellationToken.None).ConfigureAwait(false);
            if (serviceUri == null)
            {
                throw new InvalidOperationException("Cannot execute CalDAV operation because no CalDAV service URI is configured.");
            }

            var username = _owner.ResolveCalDavUsername();
            var password = _owner.ResolveCalDavPassword();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Cannot execute CalDAV operation because credentials are missing.");
            }

            var connection = new CalDavConnectionSettings
            {
                ServiceUri = serviceUri,
                Username = username,
                Password = password
            };

            var remoteCalendar = new CalDavCalendar
            {
                RemoteCalendarId = assignedCalendar.RemoteCalendarId,
                Name = assignedCalendar.Name
            };

            return (connection, remoteCalendar);
        }
    }

    private static void EnsureCalendarItemDefaults(CalendarItem item, MailAccount account, string idPrefix)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        if (item.Id == Guid.Empty)
            item.Id = Guid.NewGuid();

        if (string.IsNullOrWhiteSpace(item.RemoteEventId))
            item.RemoteEventId = $"{idPrefix}-{item.Id:N}";

        if (item.CreatedAt == default)
            item.CreatedAt = DateTimeOffset.UtcNow;

        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.OrganizerDisplayName ??= account?.SenderName ?? string.Empty;
        item.OrganizerEmail ??= account?.Address ?? string.Empty;
        item.StartTimeZone ??= TimeZoneInfo.Local.Id;
        item.EndTimeZone ??= item.StartTimeZone;
    }

    private static string BuildIcsContent(CalendarItem item, List<CalendarEventAttendee> attendees)
    {
        var uid = item.RemoteEventId?.Split(new[] { "::" }, StringSplitOptions.None)[0] ?? item.Id.ToString("N");
        var dtStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Wino Mail//Calendar//EN",
            "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            $"UID:{EscapeIcs(uid)}",
            $"DTSTAMP:{dtStamp}",
        };

        if (item.IsAllDayEvent)
        {
            lines.Add($"DTSTART;VALUE=DATE:{item.StartDate:yyyyMMdd}");
            lines.Add($"DTEND;VALUE=DATE:{item.EndDate:yyyyMMdd}");
        }
        else
        {
            var startUtc = ConvertEventTimeToUtc(item.StartDate, item.StartTimeZone);
            var endUtc = ConvertEventTimeToUtc(item.EndDate, item.EndTimeZone ?? item.StartTimeZone);

            lines.Add($"DTSTART:{startUtc:yyyyMMdd'T'HHmmss'Z'}");
            lines.Add($"DTEND:{endUtc:yyyyMMdd'T'HHmmss'Z'}");
        }

        if (!string.IsNullOrWhiteSpace(item.Title))
            lines.Add($"SUMMARY:{EscapeIcs(item.Title)}");

        if (!string.IsNullOrWhiteSpace(item.Description))
            lines.Add($"DESCRIPTION:{EscapeIcs(item.Description)}");

        if (!string.IsNullOrWhiteSpace(item.Location))
            lines.Add($"LOCATION:{EscapeIcs(item.Location)}");

        lines.Add($"STATUS:{MapStatus(item.Status)}");
        lines.Add($"TRANSP:{(item.ShowAs == CalendarItemShowAs.Free ? "TRANSPARENT" : "OPAQUE")}");
        lines.Add($"CLASS:{MapVisibility(item.Visibility)}");

        if (!string.IsNullOrWhiteSpace(item.Recurrence))
        {
            var recurrenceLines = item.Recurrence
                .Split(Wino.Core.Domain.Constants.CalendarEventRecurrenceRuleSeperator, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l));

            lines.AddRange(recurrenceLines);
        }

        if (!string.IsNullOrWhiteSpace(item.OrganizerEmail))
        {
            var organizerName = string.IsNullOrWhiteSpace(item.OrganizerDisplayName)
                ? item.OrganizerEmail
                : item.OrganizerDisplayName;
            lines.Add($"ORGANIZER;CN={EscapeIcs(organizerName)}:mailto:{EscapeIcs(item.OrganizerEmail)}");
        }

        if (attendees != null)
        {
            foreach (var attendee in attendees.Where(a => !string.IsNullOrWhiteSpace(a.Email)))
            {
                var role = attendee.IsOptionalAttendee ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT";
                var partStat = attendee.AttendenceStatus switch
                {
                    AttendeeStatus.Accepted => "ACCEPTED",
                    AttendeeStatus.Declined => "DECLINED",
                    AttendeeStatus.Tentative => "TENTATIVE",
                    _ => "NEEDS-ACTION"
                };

                var cn = string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name;
                lines.Add($"ATTENDEE;CN={EscapeIcs(cn)};ROLE={role};PARTSTAT={partStat}:mailto:{EscapeIcs(attendee.Email)}");
            }
        }

        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");

        return string.Join(Environment.NewLine, lines);
    }

    private static DateTime ConvertEventTimeToUtc(DateTime eventDateTime, string eventTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(eventTimeZoneId))
            return eventDateTime.ToUniversalTime();

        try
        {
            var eventTimeZone = TimeZoneInfo.FindSystemTimeZoneById(eventTimeZoneId);
            var unspecifiedDateTime = DateTime.SpecifyKind(eventDateTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecifiedDateTime, eventTimeZone);
        }
        catch
        {
            return eventDateTime.ToUniversalTime();
        }
    }

    private static string EscapeIcs(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string MapStatus(CalendarItemStatus status)
    {
        return status switch
        {
            CalendarItemStatus.Cancelled => "CANCELLED",
            CalendarItemStatus.Tentative => "TENTATIVE",
            _ => "CONFIRMED"
        };
    }

    private static string MapVisibility(CalendarItemVisibility visibility)
    {
        return visibility switch
        {
            CalendarItemVisibility.Public => "PUBLIC",
            CalendarItemVisibility.Private => "PRIVATE",
            CalendarItemVisibility.Confidential => "CONFIDENTIAL",
            _ => "PUBLIC"
        };
    }

    public Task StartIdleClientAsync()
    {
        if (IsDisposing)
            return Task.CompletedTask;

        if (_idleLoopTask != null && !_idleLoopTask.IsCompleted)
            return Task.CompletedTask;

        _idleLoopCancellationTokenSource = new CancellationTokenSource();
        _idleLoopTask = RunIdleLoopAsync(_idleLoopCancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    private async Task RunIdleLoopAsync(CancellationToken cancellationToken)
    {
        int reconnectAttempt = 0;

        while (!cancellationToken.IsCancellationRequested && !IsDisposing)
        {
            IImapClient idleClient = null;
            IMailFolder inboxFolder = null;
            bool shouldReconnect = false;

            try
            {
                idleClient = await _clientPool.GetIdleClientAsync(cancellationToken).ConfigureAwait(false);

                if (idleClient == null)
                {
                    _logger.Warning("Dedicated IDLE client could not be allocated for {AccountName}.", Account.Name);
                    return;
                }

                if (!idleClient.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    _logger.Information("{AccountName} does not support IMAP IDLE. Automatic updates rely on global sync interval.", Account.Name);
                    return;
                }

                if (idleClient.Inbox == null)
                {
                    _logger.Warning("{AccountName} does not expose Inbox for IDLE listening.", Account.Name);
                    return;
                }

                inboxFolder = idleClient.Inbox;

                await inboxFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

                _lastIdleInboxCount = inboxFolder.Count;
                inboxFolder.CountChanged += IdleInboxCountChanged;

                reconnectAttempt = 0;
                _logger.Debug("Started dedicated IDLE loop for {AccountName}.", Account.Name);

                while (!cancellationToken.IsCancellationRequested && !IsDisposing && idleClient.IsConnected)
                {
                    using var idleDoneTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                    await idleClient.IdleAsync(idleDoneTokenSource.Token, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ImapProtocolException protocolException)
            {
                _logger.Information(protocolException, "Idle client received protocol exception for {AccountName}.", Account.Name);
                shouldReconnect = true;
            }
            catch (IOException ioException)
            {
                _logger.Information(ioException, "Idle client received IO exception for {AccountName}.", Account.Name);
                shouldReconnect = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsDisposing)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                shouldReconnect = true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Idle client loop failed for {AccountName}.", Account.Name);
                shouldReconnect = true;
            }
            finally
            {
                if (inboxFolder != null)
                {
                    inboxFolder.CountChanged -= IdleInboxCountChanged;

                    if (inboxFolder.IsOpen && !cancellationToken.IsCancellationRequested)
                    {
                        await inboxFolder.CloseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }

                _clientPool.ReleaseIdleClient(isFaulted: shouldReconnect);
            }

            if (!shouldReconnect)
            {
                break;
            }

            reconnectAttempt++;
            var reconnectDelay = GetIdleReconnectDelay(reconnectAttempt);
            _logger.Information("Reconnecting IDLE client for {AccountName} in {Delay}.", Account.Name, reconnectDelay);

            try
            {
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static TimeSpan GetIdleReconnectDelay(int attempt)
    {
        var backoffSeconds = Math.Min(60, Math.Pow(2, Math.Min(attempt, 6)));
        int jitterMs;

        lock (IdleReconnectJitter)
        {
            jitterMs = IdleReconnectJitter.Next(250, 1250);
        }

        return TimeSpan.FromSeconds(backoffSeconds) + TimeSpan.FromMilliseconds(jitterMs);
    }

    private void RequestIdleChangeSynchronization()
    {
        if (!ShouldTriggerIdleSynchronization(DateTime.UtcNow))
            return;

        var options = new MailSynchronizationOptions()
        {
            AccountId = Account.Id,
            Type = MailSynchronizationType.IMAPIdle
        };

        WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(options));
    }

    internal bool ShouldTriggerIdleSynchronization(DateTime nowUtc)
    {
        lock (_idleDebounceLock)
        {
            if (nowUtc - _lastIdleSyncRequestUtc < _idleSyncDebounceWindow)
            {
                return false;
            }

            _lastIdleSyncRequestUtc = nowUtc;
            return true;
        }
    }

    private void IdleInboxCountChanged(object sender, EventArgs e)
    {
        if (sender is not IMailFolder inboxFolder)
            return;

        var currentCount = inboxFolder.Count;
        var previousCount = _lastIdleInboxCount;
        _lastIdleInboxCount = currentCount;

        if (currentCount > previousCount)
        {
            RequestIdleChangeSynchronization();
        }
    }

    public async Task StopIdleClientAsync()
    {
        if (_idleLoopCancellationTokenSource != null)
        {
            _idleLoopCancellationTokenSource.Cancel();
        }

        if (_idleLoopTask != null)
        {
            try
            {
                await _idleLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // no-op
            }
        }

        _idleLoopCancellationTokenSource?.Dispose();
        _idleLoopCancellationTokenSource = null;
        _idleLoopTask = null;
    }

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync();
        await StopIdleClientAsync();

        // Make sure the client pool safely disconnects all ImapClients.
        _clientPool.Dispose();
    }

    public Task PreWarmClientPoolAsync() => _clientPool.PreWarmPoolAsync();
}



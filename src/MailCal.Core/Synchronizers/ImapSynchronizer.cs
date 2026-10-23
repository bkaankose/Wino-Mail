using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Serilog;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Exceptions;
using MailCal.Core.Domain.Extensions;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Connectivity;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.MailItem;
using MailCal.Core.Domain.Models.Synchronization;
using MailCal.Core.Diagnostics;
using MailCal.Core.Extensions;
using MailCal.Core.Helpers;
using MailCal.Core.Integration;
using MailCal.Core.Integration.Processors;
using MailCal.Core.Requests.Bundles;
using MailCal.Core.Requests.Calendar;
using MailCal.Core.Requests.Folder;
using MailCal.Core.Requests.Mail;
using MailCal.Core.Synchronizers.CalDav;
using MailCal.Core.Synchronizers.ImapSync;
using MailCal.Core.Misc;
using MailCal.Core.Services;
using MailCal.Messaging.Server;
using MailCal.Messaging.UI;
using MailCal.Services;
using MailCal.Services.Extensions;

namespace MailCal.Core.Synchronizers.Mail;

public partial class ImapSynchronizer : MailCalSynchronizer<ImapRequest, ImapMessageCreationPackage, object, AccountContact>, IImapSynchronizer
{
    private readonly LocalContactSynchronizer _localContactSynchronizer;
    private readonly LocalTaskSynchronizer _localTaskSynchronizer;
    private readonly ICardDavSynchronizationEngine _cardDavSynchronizationEngine;

    protected override Task ExecuteContactRequestsInternalAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
        => Account.ContactIntegrationSource switch
        {
            AccountIntegrationSource.Dav when _cardDavSynchronizationEngine is not null => _cardDavSynchronizationEngine.ExecuteRequestsAsync(Account, requests, cancellationToken),
            AccountIntegrationSource.Local => _localContactSynchronizer.ExecuteRequestsAsync(requests, cancellationToken),
            _ => throw new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable)
        };

    protected override Task<ContactSynchronizationResult> SynchronizeContactsInternalAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default)
        => Account.ContactIntegrationSource == AccountIntegrationSource.Dav && _cardDavSynchronizationEngine is not null
            ? _cardDavSynchronizationEngine.SynchronizeAsync(Account, options, cancellationToken)
            : Account.ContactIntegrationSource == AccountIntegrationSource.Local
                ? _localContactSynchronizer.SynchronizeAsync(options, cancellationToken)
                : Task.FromResult(ContactSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable)));

    protected override Task ExecuteTaskRequestsInternalAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default)
        => Account.TaskIntegrationSource == AccountIntegrationSource.Local
            ? _localTaskSynchronizer.ExecuteRequestsAsync(requests, cancellationToken)
            : throw new InvalidOperationException(Translator.Synchronizer_TasksUnavailable);

    protected override Task<TaskSynchronizationResult> SynchronizeTasksInternalAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default)
        => Account.TaskIntegrationSource == AccountIntegrationSource.Local
            ? _localTaskSynchronizer.SynchronizeAsync(options, cancellationToken)
            : Task.FromResult(TaskSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_TasksUnavailable)));
    /// <summary>
    /// N/A for IMAP as it doesn't support batch modifications natively.
    /// </summary>
    protected override bool SerializeMailOperations => true;
    protected override bool DequeueMailRequestsBeforeExecution => false;

    public override uint BatchModificationSize => 1000;
    public override uint InitialMessageDownloadCountPerFolder => 500;

    #region Idle Implementation

    private static readonly Random IdleReconnectJitter = new();
    private readonly object _idleDebounceLock = new();
    private CancellationTokenSource _idleLoopCancellationTokenSource;
    private Task _idleLoopTask;
    private readonly SemaphoreSlim _idleLifecycle = new(1, 1);
    private readonly object _idleDoneLock = new();
    private CancellationTokenSource _idleDoneCancellationTokenSource;
    private volatile bool _idleStopping;
    private Task _idleNotificationTask;
    private bool _idleChangePending;
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
    private readonly ICalDavSynchronizationEngine _calDavSynchronizationEngine;
    private readonly ICalendarService _calendarService;
    private readonly IMailFilterExecutor _mailFilterExecutor;
    private readonly IServerCertificateTrustService _serverCertificateTrustService;
    private readonly ISmtpTransport _smtpTransport;
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
                            ICalendarService calendarService,
                            IMailFilterExecutor mailFilterExecutor = null,
                            IServerCertificateTrustService serverCertificateTrustService = null,
                            ICardDavSynchronizationEngine cardDavSynchronizationEngine = null,
                            IContactService contactService = null,
                            ITaskService taskService = null,
                            ISmtpTransport smtpTransport = null,
                            ICalDavSynchronizationEngine calDavSynchronizationEngine = null) : base(account, WeakReferenceMessenger.Default)
    {
        _imapChangeProcessor = imapChangeProcessor;
        _applicationConfiguration = applicationConfiguration;
        _unifiedSynchronizer = unifiedSynchronizer;
        _errorHandlerFactory = errorHandlerFactory;
        _calendarService = calendarService;
        _calDavSynchronizationEngine = calDavSynchronizationEngine
                                       ?? new CalDavSynchronizationEngine(calDavClient, imapChangeProcessor, calendarService, autoDiscoveryService);
        _mailFilterExecutor = mailFilterExecutor;
        _serverCertificateTrustService = serverCertificateTrustService;
        _smtpTransport = smtpTransport ?? new MailKitSmtpTransport(applicationConfiguration, serverCertificateTrustService);
        _cardDavSynchronizationEngine = cardDavSynchronizationEngine;
        _localContactSynchronizer = new LocalContactSynchronizer(contactService);
        _localTaskSynchronizer = new LocalTaskSynchronizer(taskService);

        var poolOptions = ImapClientPoolOptions.CreateDefault(
            Account.ServerInformation,
            Account.IsProtocolLogEnabled
                ? () => MailCalProtocolLogger.CreateAccountLogger(
                    _applicationConfiguration.ApplicationDataFolderPath,
                    Account.Id,
                    MailProtocol.Imap)
                : null,
            _serverCertificateTrustService,
            Account);

        _clientPool = new ImapClientPool(poolOptions);
        _localCalendarOperationHandler = new LocalCalendarOperationHandler(Account, _imapChangeProcessor, _calendarService, _applicationConfiguration.ApplicationDataFolderPath, "local");
        _calDavCalendarOperationHandler = new CalDavCalendarOperationHandler(Account, _calDavSynchronizationEngine);
    }

    /// <summary>
    /// Returns UniqueId for the given mail copy id.
    /// </summary>
    private UniqueId GetUniqueId(string mailCopyId) => new(MailkitClientExtensions.ResolveUid(mailCopyId));
    private UniqueId GetUniqueId(MailCopy mailCopy) => MailkitClientExtensions.ResolveUidStruct(mailCopy);

    internal static void EnsureMessageUidValidity(IMailFolder remoteFolder, IEnumerable<MailCopy> mails)
    {
        foreach (var mail in mails)
        {
            var expected = mail.ImapUidValidity != 0 ? mail.ImapUidValidity : mail.AssignedFolder?.UidValidity ?? 0;
            if (expected == 0 || expected != remoteFolder.UidValidity)
                throw new InvalidOperationException("The IMAP mailbox identity changed. Synchronize the folder before retrying this operation.");
        }
    }

    private async Task DeleteLocalCopiesMissingFromRemoteAsync(
        IMailFolder remoteFolder,
        IEnumerable<(MailCopy MailCopy, UniqueId UniqueId)> candidates,
        CancellationToken cancellationToken = default)
    {
        var candidateList = candidates?
            .Where(candidate => candidate.MailCopy != null && candidate.UniqueId.IsValid)
            .GroupBy(candidate => candidate.UniqueId.Id)
            .Select(group => group.First())
            .ToList() ?? [];

        if (candidateList.Count == 0)
            return;

        var requestedUids = candidateList.Select(candidate => candidate.UniqueId).ToList();
        var existingUids = await remoteFolder
            .SearchAsync(SearchQuery.Uids(new UniqueIdSet(requestedUids, SortOrder.Ascending)).And(SearchQuery.NotDeleted), cancellationToken)
            .ConfigureAwait(false);

        var existingUidSet = existingUids.Select(uid => uid.Id).ToHashSet();
        var confirmedMissing = candidateList
            .Where(candidate => !existingUidSet.Contains(candidate.UniqueId.Id))
            .ToList();

        foreach (var candidate in confirmedMissing)
        {
            await _imapChangeProcessor.DeleteMailAsync(Account.Id, candidate.MailCopy.Id).ConfigureAwait(false);
        }

        foreach (var candidate in candidateList.Except(confirmedMissing))
        {
            _logger.Warning(
                "IMAP message {MailId} with UID {Uid} still exists in folder {FolderName} after remote operation. Keeping local copy.",
                candidate.MailCopy.Id,
                candidate.UniqueId.Id,
                candidate.MailCopy.AssignedFolder?.FolderName);
        }
    }

    #region Mail Integrations

    // Items are grouped before being passed to this method.
    // Meaning that all items will come from and to the same folder.
    // It's fine to assume that here.

    public override List<IRequestBundle<ImapRequest>> Move(BatchMoveRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return CreateSingleTaskBundle(async (client, _, cancellationToken) =>
        {
            var sourceFolder = await client.GetFolderAsync(requests[0].FromFolder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
            var destinationFolder = await client.GetFolderAsync(requests[0].ToFolder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
            var candidates = requests.Select(item => (item.Item, UniqueId: GetUniqueId(item.Item))).ToList();
            var uniqueIds = candidates.Select(item => item.UniqueId).ToList();

            await sourceFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureMessageUidValidity(sourceFolder, requests.Select(request => request.Item));
                if (client.Capabilities.HasFlag(ImapCapabilities.Move) || client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                {
                    await sourceFolder.MoveToAsync(uniqueIds, destinationFolder, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Legacy servers cannot atomically expunge just our UIDs. Keep other clients' deletions safe.
                    await sourceFolder.CopyToAsync(uniqueIds, destinationFolder, cancellationToken).ConfigureAwait(false);
                    await sourceFolder.StoreAsync(uniqueIds,
                        new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, cancellationToken).ConfigureAwait(false);
                }
                await DeleteLocalCopiesMissingFromRemoteAsync(sourceFolder, candidates, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await client.CloseSelectedMailboxAsync(sourceFolder, _logger, cancellationToken).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<ImapRequest>> ChangeFlag(BatchChangeFlagRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return CreateSingleTaskBundle(async (client, _, cancellationToken) =>
        {
            var folder = requests[0].Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
            var uniqueIds = requests.Select(item => GetUniqueId(item.Item)).ToList();
            var request = new StoreFlagsRequest(requests[0].IsFlagged ? StoreAction.Add : StoreAction.Remove, MessageFlags.Flagged)
            {
                Silent = true
            };

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureMessageUidValidity(remoteFolder, requests.Select(request => request.Item));
                await remoteFolder.StoreAsync(uniqueIds, request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await client.CloseSelectedMailboxAsync(remoteFolder, _logger, cancellationToken).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<ImapRequest>> Delete(BatchDeleteRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return CreateSingleTaskBundle(async (client, _, cancellationToken) =>
        {
            var folder = requests[0].Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
            var candidates = requests.Select(request => (request.Item, UniqueId: GetUniqueId(request.Item))).ToList();
            var uniqueIds = candidates.Select(item => item.UniqueId).ToList();
            var storeRequest = new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true };

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureMessageUidValidity(remoteFolder, requests.Select(request => request.Item));
                await remoteFolder.StoreAsync(uniqueIds, storeRequest, cancellationToken).ConfigureAwait(false);
                if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                    await remoteFolder.ExpungeAsync(uniqueIds, cancellationToken).ConfigureAwait(false);
                await DeleteLocalCopiesMissingFromRemoteAsync(remoteFolder, candidates, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await client.CloseSelectedMailboxAsync(remoteFolder, _logger, cancellationToken).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<ImapRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return CreateSingleTaskBundle(async (client, _, cancellationToken) =>
        {
            var folder = requests[0].Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId, cancellationToken).ConfigureAwait(false);
            var uniqueIds = requests.Select(request => GetUniqueId(request.Item)).ToList();
            var storeRequest = new StoreFlagsRequest(requests[0].IsRead ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen)
            {
                Silent = true
            };

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureMessageUidValidity(remoteFolder, requests.Select(request => request.Item));
                await remoteFolder.StoreAsync(uniqueIds, storeRequest, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await client.CloseSelectedMailboxAsync(remoteFolder, _logger, cancellationToken).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<ImapRequest>> CreateDraft(CreateDraftRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            var remoteDraftFolder = await client.GetFolderAsync(request.DraftPreperationRequest.CreatedLocalDraftCopy.AssignedFolder.RemoteFolderId).ConfigureAwait(false);

            await remoteDraftFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            try
            {
                var appendedUid = await remoteDraftFolder
                    .AppendAsync(request.DraftPreperationRequest.CreatedLocalDraftMimeMessage, MessageFlags.Draft)
                    .ConfigureAwait(false);

                if (appendedUid.HasValue)
                {
                    var localDraft = request.DraftPreperationRequest.CreatedLocalDraftCopy;
                    var isMapped = await _imapChangeProcessor.MapLocalDraftAsync(
                        Account.Id,
                        localDraft.UniqueId,
                        MailkitClientExtensions.CreateUid(localDraft.AssignedFolder.Id, appendedUid.Value.Id),
                        localDraft.UniqueId.ToString(),
                        localDraft.ThreadId,
                        appendedUid.Value.Id,
                        remoteDraftFolder.UidValidity).ConfigureAwait(false);

                    if (!isMapped)
                    {
                        // The local draft was discarded while APPEND was in flight. Remove the
                        // appended message before the folder sync can import it as a new draft.
                        await remoteDraftFolder
                            .AddFlagsAsync(appendedUid.Value, MessageFlags.Deleted, true)
                            .ConfigureAwait(false);
                        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                            await remoteDraftFolder.ExpungeAsync([appendedUid.Value]).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await remoteDraftFolder.CloseAsync().ConfigureAwait(false);
            }
        }, request, request);
    }

    public override List<IRequestBundle<ImapRequest>> Archive(BatchArchiveRequest request)
    {
        var batchMoveRequest = new BatchMoveRequest(request.Select(item => new MoveRequest(item.Item, item.FromFolder, item.ToFolder)));
        return BindOriginalRequests(Move(batchMoveRequest), request.Cast<IRequestBase>().ToList());
    }

    public override List<IRequestBundle<ImapRequest>> EmptyFolder(EmptyFolderRequest request)
        => BindOriginalRequests(Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a)))), new[] { request });

    public override List<IRequestBundle<ImapRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => BindOriginalRequests(MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true)))), new[] { request });

    private static List<IRequestBundle<ImapRequest>> BindOriginalRequests(
        List<IRequestBundle<ImapRequest>> bundles, IReadOnlyList<IRequestBase> originals)
    {
        if (bundles.Count == 0 && originals.Count > 0)
        {
            var request = originals[0];
            bundles.Add(new ImapRequestBundle(new ImapRequest((_, _) => Task.CompletedTask, request,
                requiresConnectedClient: false), request, request));
        }
        foreach (var bundle in bundles)
            bundle.NativeRequest.QueuedRequests = originals;
        return bundles;
    }

    public override List<IRequestBundle<ImapRequest>> SendDraft(SendDraftRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            // Batch sending is not supported. It will always be a single request therefore no need for a loop here.

            var singleRequest = request.Request;

            var smtpMessage = await _smtpTransport.SendAsync(Account, singleRequest.Mime).ConfigureAwait(false);

            await FinalizeAcceptedDraftAsync(client, singleRequest, smtpMessage).ConfigureAwait(false);
        }, request, request);
    }

    private async Task FinalizeAcceptedDraftAsync(
        IImapClient client,
        SendDraftPreparationRequest request,
        MimeMessage smtpMessage)
    {
        var draft = request.MailItem;
        var draftUid = GetUniqueId(draft);

        try
        {
            var remoteDraftFolder = await client
                .GetFolderAsync(draft.AssignedFolder.RemoteFolderId)
                .ConfigureAwait(false);

            await remoteDraftFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            try
            {
                EnsureMessageUidValidity(remoteDraftFolder, new[] { draft });
                await DeleteRemoteDraftIfPresentAsync(remoteDraftFolder, draftUid,
                    canExpunge: client.Capabilities.HasFlag(ImapCapabilities.UidPlus)).ConfigureAwait(false);
            }
            finally
            {
                await client.CloseSelectedMailboxAsync(remoteDraftFolder, _logger).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "SMTP accepted message {MailId}, but IMAP draft cleanup failed for folder {FolderName}, UID {Uid}.",
                draft.Id,
                draft.AssignedFolder?.FolderName,
                draftUid.Id);
        }

        // Do not wait for a later folder reconciliation to observe the deletion. SMTP acceptance
        // is the commit point for sending, so the local draft must be removed immediately even
        // when the provider already removed the remote UID as a side effect of SMTP submission.
        try
        {
            await _imapChangeProcessor.DeleteMailAsync(Account.Id, draft.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "SMTP accepted message {MailId}, but deleting its local draft copy failed for account {AccountId}.",
                draft.Id,
                Account.Id);
        }

        if (!request.AccountPreferences.ShouldAppendMessagesToSentFolder || request.SentFolder == null)
            return;

        try
        {
            var sentFolder = await client
                .GetFolderAsync(request.SentFolder.RemoteFolderId)
                .ConfigureAwait(false);

            await sentFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            try
            {
                await sentFolder.AppendAsync(smtpMessage, MessageFlags.Seen).ConfigureAwait(false);
            }
            finally
            {
                await client.CloseSelectedMailboxAsync(sentFolder, _logger).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "SMTP accepted message {MailId}, but appending its Sent copy failed for folder {FolderName}.",
                draft.Id,
                request.SentFolder.FolderName);
        }
    }

    internal static async Task<bool> DeleteRemoteDraftIfPresentAsync(
        IMailFolder remoteDraftFolder,
        UniqueId draftUid,
        CancellationToken cancellationToken = default,
        bool canExpunge = false)
    {
        ArgumentNullException.ThrowIfNull(remoteDraftFolder);

        var matchingUids = await remoteDraftFolder
            .SearchAsync(SearchQuery.Uids(new UniqueIdSet([draftUid], SortOrder.Ascending)), cancellationToken)
            .ConfigureAwait(false);

        if (!matchingUids.Contains(draftUid))
            return false;

        var storeRequest = new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted)
        {
            Silent = true
        };

        await remoteDraftFolder
            .StoreAsync([draftUid], storeRequest, cancellationToken)
            .ConfigureAwait(false);
        if (canExpunge)
            await remoteDraftFolder.ExpungeAsync([draftUid], cancellationToken).ConfigureAwait(false);

        return true;
    }

    internal static MimeMessage CreateSmtpMessage(MimeMessage draftMessage)
        => MailKitSmtpTransport.CreateSmtpMessage(draftMessage);

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem,
                                                           ITransferProgress transferProgress = null,
                                                           CancellationToken cancellationToken = default)
    {
        var folder = mailItem.AssignedFolder;
        var remoteFolderId = folder.RemoteFolderId;

        var client = await _clientPool.GetClientAsync(cancellationToken, purpose: "MimeDownload").ConfigureAwait(false);

        bool faulted = true;
        try
        {
            var remoteFolder = await client.GetFolderAsync(remoteFolderId, cancellationToken).ConfigureAwait(false);

            var uniqueId = GetUniqueId(mailItem);

            await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            EnsureMessageUidValidity(remoteFolder, new[] { mailItem });
            var message = (await remoteFolder.GetMessageAsync(uniqueId, cancellationToken, transferProgress).ConfigureAwait(false)).RecoverHeaderCharsets();

            await _imapChangeProcessor.SaveMimeFileAsync(mailItem.FileId, message, Account.Id, mailItem.Id).ConfigureAwait(false);
            await client.CloseSelectedMailboxAsync(remoteFolder, _logger, cancellationToken).ConfigureAwait(false);
            faulted = false;
        }
        finally
        {
            _clientPool.Release(client, destroyClient: faulted);
        }
    }

    /// <summary>
    /// Local calendars keep their attachments on disk already, so a download copies the stored file.
    /// CalDAV attachments are downloaded by <see cref="ICalDavSynchronizationEngine"/>.
    /// </summary>
    public override async Task DownloadCalendarAttachmentAsync(
        MailCal.Core.Domain.Entities.Calendar.CalendarItem calendarItem,
        MailCal.Core.Domain.Entities.Calendar.CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarItem);
        ArgumentNullException.ThrowIfNull(attachment);

        if (Account.GetEffectiveCalendarIntegrationSource() == AccountIntegrationSource.Local)
        {
            await LocalCalendarOperationHandler.CopyStoredAttachmentAsync(attachment, localFilePath, _calendarService).ConfigureAwait(false);
            return;
        }

        await _calDavSynchronizationEngine
            .DownloadAttachmentAsync(Account, calendarItem, attachment, localFilePath, cancellationToken)
            .ConfigureAwait(false);
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
        var source = Account.GetEffectiveCalendarIntegrationSource();
        var mode = Account.ServerInformation?.CalendarSupportMode ?? ImapCalendarSupportMode.Disabled;

        return source switch
        {
            AccountIntegrationSource.Local => _localCalendarOperationHandler,
            AccountIntegrationSource.Dav when Account.ProviderType == MailProviderType.IMAP4 &&
                                              Account.IsCalendarAccessGranted &&
                                              mode == ImapCalendarSupportMode.CalDav => _calDavCalendarOperationHandler,
            _ => throw new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable)
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

        var draftHeaderValue = message.MimeMessage?.Headers?.FirstOrDefault(h => Domain.Constants.IsLocalDraftHeader(h.Field))?.Value
                               ?? message.MessageSummary?.Headers?.FirstOrDefault(h => Domain.Constants.IsLocalDraftHeader(h.Field))?.Value;

        if (Guid.TryParse(draftHeaderValue, out Guid localDraftCopyUniqueId))
        {
            // This message belongs to existing local draft copy.
            // We don't need to create a new mail copy for this message, just update the existing one.

            bool isMappingSuccessful = await _imapChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopyUniqueId, mailCopy.Id, draftHeaderValue, mailCopy.ThreadId);

            if (isMappingSuccessful) return null;

            if (await _imapChangeProcessor.IsMailExistsInFolderAsync(mailCopy.Id, assignedFolder.Id).ConfigureAwait(false) ||
                await _imapChangeProcessor.IsMailExistsAsync(Account.Id, localDraftCopyUniqueId).ConfigureAwait(false))
            {
                _logger.Debug("Skipping duplicate remote draft {RemoteId} for local draft {LocalId}",
                    mailCopy.Id, localDraftCopyUniqueId);
                return null;
            }

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
        var filterCandidateIds = new List<string>();
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
                using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var linkedToken = linkedCancellationTokenSource.Token;
                var resultLock = new object();
                var pendingFolders = new ConcurrentQueue<MailItemFolder>(synchronizationFolders);
                int completedFolders = 0;

                // A delta sync of an already-synchronized folder costs a few round trips, which is far
                // less than the TLS and AUTH handshake for an extra socket. One warm client walks the
                // queue; extra workers only join when a spare connection already exists in the pool.
                // Only an initial sync with long FETCH streams justifies opening more sockets.
                // Folders synchronized before this stamp existed have no date but do carry a UID checkpoint.
                var isInitialSync = synchronizationFolders.Any(folder =>
                    !folder.LastSynchronizedDate.HasValue && folder.HighestKnownUid == 0);
                var workerCount = Math.Min(maxParallelFolderSyncClients, Math.Max(1, totalFolders));

                async Task RunFolderWorkerAsync(bool allowCreate)
                {
                    var client = await _clientPool.RentForReadAsync(linkedToken, allowCreate, "FolderSync").ConfigureAwait(false);
                    if (client == null) return;

                    try
                    {
                        while (!linkedToken.IsCancellationRequested && pendingFolders.TryDequeue(out var folder))
                        {
                            // A retired connection is replaced even by a secondary worker; the pool did not grow.
                            client ??= await _clientPool.RentForReadAsync(linkedToken, purpose: "FolderSyncReplacement").ConfigureAwait(false);

                            var healthy = await SynchronizeQueuedFolderAsync(client, folder).ConfigureAwait(false);
                            if (!healthy)
                            {
                                _clientPool.Release(client, destroyClient: true);
                                client = null;
                            }
                        }
                    }
                    catch
                    {
                        if (client != null)
                        {
                            _clientPool.Release(client, destroyClient: true);
                            client = null;
                        }
                        throw;
                    }
                    finally
                    {
                        if (client != null) _clientPool.Release(client);
                    }
                }

                async Task<bool> SynchronizeQueuedFolderAsync(IImapClient client, MailItemFolder folder)
                {
                    try
                    {
                        var wasPreviouslySynchronized = folder.LastSynchronizedDate.HasValue;
                        var folderResult = await _unifiedSynchronizer
                            .SynchronizeFolderAsync(
                                client,
                                folder,
                                this,
                                Account.ServerInformation?.IncomingServer,
                                linkedToken,
                                suppressMatchingLocalFilters: wasPreviouslySynchronized)
                            .ConfigureAwait(false);

                        List<string> folderDownloadedIds = null;
                        if (folderResult.Success)
                        {
                            if (folderResult.DownloadedCount > 0)
                                folderDownloadedIds = await GetDownloadedIdsForFolderAsync(folder, folderResult.DownloadedCount).ConfigureAwait(false);

                            await _imapChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);
                        }

                        lock (resultLock)
                        {
                            folderResults.Add(folderResult);
                            if (folderDownloadedIds != null && folderDownloadedIds.Count > 0)
                            {
                                downloadedMessageIds.AddRange(folderDownloadedIds);
                                if (wasPreviouslySynchronized)
                                    filterCandidateIds.AddRange(folderDownloadedIds);
                            }
                        }

                        return folderResult.Success;
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

                        if (!errorContext.CanContinueSync && errorContext.Severity != SynchronizerErrorSeverity.Transient && !ImapClientPool.IsConnectionFailure(ex))
                        {
                            _logger.Error(ex, "Folder {FolderName} sync failed with fatal error", folder.FolderName);
                            linkedCancellationTokenSource.Cancel();
                            throw;
                        }

                        _logger.Warning(ex, "Folder {FolderName} sync failed, continuing with other folders", folder.FolderName);
                        return false;
                    }
                    finally
                    {
                        var completed = Interlocked.Increment(ref completedFolders);
                        UpdateSyncProgress(totalFolders, totalFolders - completed, $"Syncing {folder.FolderName}...");
                    }
                }

                var syncTasks = new List<Task>(workerCount) { RunFolderWorkerAsync(allowCreate: true) };
                for (var i = 1; i < workerCount; i++)
                    syncTasks.Add(RunFolderWorkerAsync(allowCreate: isInitialSync));

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

            // Spare connections for interactive work are opened only after the sync no longer
            // competes for the creation gate.
            _ = _clientPool.PreWarmPoolAsync();
        }

        // Get all unread new downloaded items and return in the result.
        // This is primarily used in notifications.

        var suppressedIds = _mailFilterExecutor == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : await _mailFilterExecutor.ProcessNewMessagesAsync(Account.Id, filterCandidateIds, cancellationToken).ConfigureAwait(false);
        var unreadNewItems = await _imapChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);
        unreadNewItems.RemoveAll(item => suppressedIds.Contains(item.Id));

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
        foreach (var item in batchedRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IImapClient executorClient = null;
            var isCrashed = false;
            var started = false;
            try
            {
                if (item.NativeRequest.RequiresConnectedClient)
                    executorClient = await _clientPool.GetClientAsync(cancellationToken, purpose: "RequestExecution").ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                // Keep unstarted work in memory if acquiring a connection or an earlier bundle fails.
                // Once execution starts, an ambiguous mutation must be reconciled, never blindly replayed.
                RemoveQueuedRequests(item.NativeRequest.QueuedRequests);
                started = true;
                ApplyOptimisticUiChanges(new List<IRequestBundle<ImapRequest>> { item }, ShouldApplyOptimisticUIChanges);
                isCrashed = true;
                await item.NativeRequest.ExecuteAsync(executorClient, item.Request, cancellationToken).ConfigureAwait(false);
                isCrashed = false;
            }
            catch (Exception ex)
            {
                if (!started)
                    throw;

                if (item.Request is CreateDraftRequest createDraftRequest)
                {
                    await _imapChangeProcessor
                        .MarkDraftSyncFailedAsync(createDraftRequest.Item.UniqueId, ex.Message)
                        .ConfigureAwait(false);
                }

                var errorContext = new SynchronizerErrorContext
                {
                    Account = Account,
                    ErrorCode = ex is FolderNotFoundException ? 404 : null,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    RequestBundle = item,
                    Request = item.Request,
                    OperationType = "RequestExecution",
                    IsEntityNotFound = ex is SynchronizerEntityNotFoundException
                };

                var handled = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                CaptureSynchronizationIssue(errorContext);

                if (ShouldApplyOptimisticUIChanges(item.Request))
                    RequestUiChangeCoordinator.RevertBundle(item);

                // Handled describes error classification, not successful completion. The issue remains
                // visible and the normal source/destination synchronization resolves an uncertain mutation.
                if (!handled || ex is OperationCanceledException)
                    throw;
            }
            finally
            {
                if (executorClient != null)
                {
                    _clientPool.Release(executorClient, destroyClient: isCrashed);
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

    protected override Task MarkDraftSyncFailedAsync(Guid mailUniqueId, string error)
        => _imapChangeProcessor.MarkDraftSyncFailedAsync(mailUniqueId, error);

    /// <summary>
    /// Compares by name because the parent instance is not guaranteed to be the client's Inbox instance.
    /// </summary>
    private static bool IsInboxChild(IMailFolder remoteFolder, IMailFolder inbox)
        => inbox != null &&
           remoteFolder.ParentFolder != null &&
           string.Equals(remoteFolder.ParentFolder.FullName, inbox.FullName, StringComparison.Ordinal);

    private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken = default)
    {
        // https://www.rfc-editor.org/rfc/rfc4549#section-1.1

        var localFolders = await _imapChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);

        IImapClient executorClient = null;
        bool faulted = true;

        try
        {
            List<MailItemFolder> insertedFolders = new();
            List<MailItemFolder> updatedFolders = new();
            List<MailItemFolder> deletedFolders = new();

            var remoteFolders = new List<IMailFolder>();
            executorClient = await _clientPool.RentForReadAsync(async (candidate, token) =>
            {
                remoteFolders.Clear();
                foreach (var personalNamespace in candidate.PersonalNamespaces)
                    remoteFolders.AddRange(await candidate.GetFoldersAsync(personalNamespace,
                        cancellationToken: token).ConfigureAwait(false));
            }, cancellationToken, purpose: "FolderStructure").ConfigureAwait(false);

            remoteFolders = remoteFolders
                .GroupBy(folder => folder.FullName, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            // A successful listing can establish folder absence. UIDVALIDITY is checked during
            // message synchronization so the folder identity and user preferences survive a reset.
            if (executorClient.Inbox != null && !remoteFolders.Any(remote => remote.FullName == executorClient.Inbox.FullName))
                remoteFolders.Add(executorClient.Inbox);

            foreach (var localFolder in localFolders.ToList())
            {
                if (!remoteFolders.Any(remote => remote.FullName == localFolder.RemoteFolderId))
                {
                    await _imapChangeProcessor.DeleteFolderAsync(Account.Id, localFolder.RemoteFolderId).ConfigureAwait(false);
                    deletedFolders.Add(localFolder);
                    localFolders.Remove(localFolder);
                }
            }

            // 2. Get all remote folders and insert/update each of them.

            IMailFolder inbox = executorClient.Inbox;

            // Sometimes Inbox is the root namespace. We need to check for that.
            if (inbox != null && !remoteFolders.Contains(inbox))
                remoteFolders.Add(inbox);

            var selectableRemoteFolders = remoteFolders
                .Where(remoteFolder => (!remoteFolder.IsNamespace || remoteFolder.Attributes.HasFlag(FolderAttributes.Inbox)) &&
                                       remoteFolder.Exists &&
                                       remoteFolder.CanOpen)
                .ToList();
            var knownFolderMap = _unifiedSynchronizer.ResolveKnownFolders(
                executorClient,
                Account,
                selectableRemoteFolders,
                localFolders);

            foreach (var remoteFolder in selectableRemoteFolders)
            {
                var existingLocalFolder = localFolders.FirstOrDefault(a => a.RemoteFolderId == remoteFolder.FullName);

                if (existingLocalFolder == null)
                {
                    // Folder doesn't exist locally. Insert it.

                    var localFolder = remoteFolder.GetLocalFolder();

                    var resolvedRole = knownFolderMap.GetValueOrDefault(remoteFolder.FullName, SpecialFolderType.Other);
                    ApplyFolderRole(localFolder, resolvedRole);

                    localFolder.MailAccountId = Account.Id;

                    // Sometimes sub folders are parented under Inbox.
                    // Even though this makes sense in server level, in the client it sucks.
                    // That will make sub folders to be parented under Inbox in the client.
                    // Instead, we will mark them as non-parented folders.
                    // This is better. Model allows personalized folder structure anyways
                    // even though we don't have the page/control to adjust it.

                    if (IsInboxChild(remoteFolder, inbox))
                        localFolder.ParentRemoteFolderId = string.Empty;

                    // A new folder starts without a UID checkpoint. SELECT during sync establishes it.
                    localFolder.UidValidity = 0;

                    insertedFolders.Add(localFolder);
                }
                else
                {
                    // Update existing folder. Right now we only update the name.

                    var parentId = IsInboxChild(remoteFolder, inbox) ? string.Empty : remoteFolder.ParentFolder?.FullName ?? string.Empty;
                    var needsUpdate = existingLocalFolder.ParentRemoteFolderId != parentId;
                    existingLocalFolder.ParentRemoteFolderId = parentId;
                    if (ShouldUpdateFolder(remoteFolder, existingLocalFolder))
                    {
                        existingLocalFolder.FolderName = remoteFolder.Name;
                        needsUpdate = true;
                    }

                    if (Account.ImapKnownFolderBootstrapState == ImapKnownFolderBootstrapState.Pending &&
                        knownFolderMap.TryGetValue(remoteFolder.FullName, out var resolvedRole) &&
                        !HasFolderRoleMetadata(existingLocalFolder, resolvedRole))
                    {
                        ApplyFolderRole(existingLocalFolder, resolvedRole);
                        needsUpdate = true;
                    }

                    if (needsUpdate)
                        updatedFolders.Add(existingLocalFolder);
                    else
                        localFolders.Remove(existingLocalFolder);
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

            if (Account.ImapKnownFolderBootstrapState == ImapKnownFolderBootstrapState.Pending)
            {
                await ImapKnownFolderBootstrap.CompleteAsync(
                    Account,
                    account => _imapChangeProcessor.UpdateAccountAsync(account)).ConfigureAwait(false);
            }

            if (insertedFolders.Any() || deletedFolders.Any() || updatedFolders.Any())
            {
                _isFolderStructureChanged = true;
            }
            faulted = false;
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
                _clientPool.Release(executorClient, destroyClient: faulted);
            }
        }
    }

    internal static void ApplyFolderRole(MailItemFolder folder, SpecialFolderType role)
    {
        var isSystemFolder = role != SpecialFolderType.Other;

        folder.SpecialFolderType = role;
        folder.IsSystemFolder = isSystemFolder;
        folder.IsSynchronizationEnabled = isSystemFolder;
        folder.IsSticky = isSystemFolder;
        folder.ShowUnreadCount = isSystemFolder && role != SpecialFolderType.Deleted;
    }

    private static bool HasFolderRoleMetadata(MailItemFolder folder, SpecialFolderType role)
    {
        var isSystemFolder = role != SpecialFolderType.Other;
        return folder.SpecialFolderType == role &&
               folder.IsSystemFolder == isSystemFolder &&
               folder.IsSynchronizationEnabled == isSystemFolder &&
               folder.IsSticky == isSystemFolder &&
               folder.ShowUnreadCount == (isSystemFolder && role != SpecialFolderType.Deleted);
    }

    public override async Task<List<MailCopy>> OnlineSearchAsync(RemoteMailSearchCriteria criteria, List<IMailItemFolder> folders, CancellationToken cancellationToken = default)
    {
        IImapClient client = null;
        bool faulted = true;

        try
        {
            client = await _clientPool.GetClientAsync(cancellationToken, purpose: "OnlineSearch").ConfigureAwait(false);

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
                var query = BuildOnlineSearchQuery(criteria);

                // A broad filter (read mail, or attachments alone, which the server cannot check)
                // can match the whole folder. Only the newest results are downloaded, as with Outlook.
                var searchResultsInFolder = (await remoteFolder.SearchAsync(query, cancellationToken).ConfigureAwait(false))
                    .OrderByDescending(uid => uid.Id)
                    .Take(MaxOnlineSearchResultsPerFolder)
                    .ToList();
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

            var result = await _imapChangeProcessor.GetMailCopiesAsync(searchResultFolderMailUids);
            faulted = false;
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to perform online imap search.");
            throw;
        }
        finally
        {
            _clientPool.Release(client, destroyClient: faulted);
        }
    }

    internal const int MaxOnlineSearchResultsPerFolder = 1000;

    internal static SearchQuery BuildOnlineSearchQuery(RemoteMailSearchCriteria criteria)
    {
        SearchQuery query = string.IsNullOrWhiteSpace(criteria.Query)
            ? SearchQuery.All
            : SearchQuery.BodyContains(criteria.Query).Or(SearchQuery.SubjectContains(criteria.Query));
        if (!string.IsNullOrWhiteSpace(criteria.Sender)) query = query.And(SearchQuery.FromContains(criteria.Sender.Trim()));
        if (!string.IsNullOrWhiteSpace(criteria.Subject)) query = query.And(SearchQuery.SubjectContains(criteria.Subject.Trim()));

        // SINCE and BEFORE compare whole days in the server's time zone. The range is one day wider
        // on each side here, and the exact range is applied to the downloaded results.
        if (criteria.ReceivedAfterUtc is { } after) query = query.And(SearchQuery.DeliveredAfter(after.UtcDateTime.Date.AddDays(-1)));
        if (criteria.ReceivedBeforeUtc is { } before) query = query.And(SearchQuery.DeliveredBefore(before.UtcDateTime.Date.AddDays(2)));
        if (criteria.ReadStatus == MailReadStatusFilter.Unread) query = query.And(SearchQuery.NotSeen);
        if (criteria.ReadStatus == MailReadStatusFilter.Read) query = query.And(SearchQuery.Seen);
        if (criteria.IsFlagged) query = query.And(SearchQuery.Flagged);

        // SEARCH has no attachment key. Attachments are read from BODYSTRUCTURE at download and
        // filtered afterwards.
        return query;
    }

    /// <summary>
    /// Whether the local folder should be updated with the remote folder.
    /// IMAP only compares folder name for now.
    /// </summary>
    /// <param name="remoteFolder">Remote folder</param>
    /// <param name="localFolder">Local folder.</param>
    public bool ShouldUpdateFolder(IMailFolder remoteFolder, MailItemFolder localFolder)
        => !localFolder.FolderName.Equals(remoteFolder.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Local calendars have nothing to download. CalDAV calendars are synchronized by
    /// <see cref="ICalDavSynchronizationEngine"/>, which also rejects accounts without CalDAV access.
    /// </summary>
    protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.GetEffectiveCalendarIntegrationSource() == AccountIntegrationSource.Local)
            return Task.FromResult(CalendarSynchronizationResult.Empty);

        return _calDavSynchronizationEngine.SynchronizeAsync(
            Account,
            options,
            (total, remaining) => UpdateSyncProgress(total, remaining, Translator.SyncAction_SynchronizingCalendarEvents),
            cancellationToken);
    }

    internal interface IImapCalendarOperationHandler
    {
        bool RequiresConnectedClient { get; }
        Task CreateCalendarEventAsync(CreateCalendarEventRequest request);
        Task UpdateCalendarEventAsync(UpdateCalendarEventRequest request);
        Task DeleteCalendarEventAsync(DeleteCalendarEventRequest request);
        Task AcceptEventAsync(AcceptEventRequest request);
        Task DeclineEventAsync(DeclineEventRequest request);
        Task TentativeEventAsync(TentativeEventRequest request);
    }

    internal sealed class LocalCalendarOperationHandler : IImapCalendarOperationHandler
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
            CalendarIcsBuilder.EnsureCalendarItemDefaults(item, _account, "local");
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
            CalendarIcsBuilder.EnsureCalendarItemDefaults(item, _account, "local");
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
            CalendarIcsBuilder.EnsureCalendarItemDefaults(item, _account, "local");
            item.AssignedCalendar ??= await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);

            var attendees = await _calendarService.GetAttendeesAsync(item.Id).ConfigureAwait(false);
            await _calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
            await PersistIcsAsync(item, attendees).ConfigureAwait(false);
        }

        private Task PersistIcsAsync(CalendarItem item, List<CalendarEventAttendee> attendees)
        {
            var resourceHref = $"{_resourceScheme}://calendar/{item.CalendarId:N}/{item.Id:N}";
            var icsContent = CalendarIcsBuilder.BuildIcsContent(item, attendees);

            return _changeProcessor.SaveCalendarItemIcsAsync(
                _account.Id,
                item.CalendarId,
                item.Id,
                item.RemoteEventId,
                resourceHref,
                DateTimeOffset.UtcNow.ToString("O"),
                icsContent);
        }

        /// <summary>A local attachment is the copy made when the event was saved; opening it copies that file.</summary>
        internal static async Task CopyStoredAttachmentAsync(CalendarAttachment attachment, string localFilePath, ICalendarService calendarService)
        {
            if (string.IsNullOrWhiteSpace(attachment.LocalFilePath) || !File.Exists(attachment.LocalFilePath))
                throw new FileNotFoundException("The local calendar attachment file is missing.", attachment.FileName);

            if (!string.Equals(Path.GetFullPath(attachment.LocalFilePath), Path.GetFullPath(localFilePath), StringComparison.Ordinal))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(localFilePath));
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.Copy(attachment.LocalFilePath, localFilePath, overwrite: true);
            }

            await calendarService.MarkAttachmentDownloadedAsync(attachment.Id, localFilePath).ConfigureAwait(false);
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

    /// <summary>Routes calendar requests of a CalDAV account to <see cref="ICalDavSynchronizationEngine"/>.</summary>
    private sealed class CalDavCalendarOperationHandler : IImapCalendarOperationHandler
    {
        private readonly MailAccount _account;
        private readonly ICalDavSynchronizationEngine _engine;

        public bool RequiresConnectedClient => false;

        public CalDavCalendarOperationHandler(MailAccount account, ICalDavSynchronizationEngine engine)
        {
            _account = account;
            _engine = engine;
        }

        public Task CreateCalendarEventAsync(CreateCalendarEventRequest request)
            => _engine.CreateEventAsync(_account, request.PreparedItem, request.PreparedEvent.Attendees, request.PreparedEvent.Reminders, request.ComposeResult?.Attachments);

        public Task UpdateCalendarEventAsync(UpdateCalendarEventRequest request)
            => _engine.UpdateEventAsync(_account, request.Item, request.Attendees, request.Attachments);

        public Task DeleteCalendarEventAsync(DeleteCalendarEventRequest request)
            => _engine.DeleteEventAsync(_account, request.Item);

        public Task AcceptEventAsync(AcceptEventRequest request)
            => _engine.RespondToEventAsync(_account, request.Item, CalendarItemStatus.Accepted);

        public Task DeclineEventAsync(DeclineEventRequest request)
            => _engine.RespondToEventAsync(_account, request.Item, CalendarItemStatus.Cancelled);

        public Task TentativeEventAsync(TentativeEventRequest request)
            => _engine.RespondToEventAsync(_account, request.Item, CalendarItemStatus.Tentative);
    }

    public async Task StartIdleClientAsync()
    {
        await _idleLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsDisposing || _idleLoopTask is { IsCompleted: false }) return;
            _idleLoopCancellationTokenSource?.Dispose();
            _idleStopping = false;
            _idleLoopCancellationTokenSource = new CancellationTokenSource();
            _idleLoopTask = RunIdleLoopAsync(_idleLoopCancellationTokenSource.Token);
        }
        finally { _idleLifecycle.Release(); }
    }

    private async Task RunIdleLoopAsync(CancellationToken cancellationToken)
    {
        int reconnectAttempt = 0;

        while (!cancellationToken.IsCancellationRequested && !IsDisposing && !_idleStopping)
        {
            // Resumed by SynchronizerFactory once the attention is cleared.
            if (Account.IsNetworkAccessBlocked())
            {
                _logger.Information("Stopped IDLE loop for {AccountName}: the account needs attention.", Account.Name);
                return;
            }

            IImapClient idleClient = null;
            IMailFolder inboxFolder = null;
            bool shouldReconnect = false;

            try
            {
                idleClient = await _clientPool.GetIdleClientAsync(cancellationToken).ConfigureAwait(false);

                if (idleClient == null)
                {
                    if (!_clientPool.SupportsIdle) return;
                    // Capacity may be occupied by foreground work. Try again after backoff.
                    throw new IOException("No pool capacity for IDLE yet.");
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
                inboxFolder.MessageFlagsChanged += IdleInboxChanged;
                inboxFolder.MessageExpunged += IdleInboxChanged;
                inboxFolder.MessagesVanished += IdleInboxChanged;

                // Reconcile changes that arrived while this listener was disconnected.
                if (reconnectAttempt > 0) RequestIdleChangeSynchronization();
                _logger.Debug("Started dedicated IDLE loop for {AccountName}.", Account.Name);

                while (!cancellationToken.IsCancellationRequested && !IsDisposing && !_idleStopping && idleClient.IsConnected)
                {
                    using var idleDoneTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                    var started = DateTime.UtcNow;
                    lock (_idleDoneLock)
                    {
                        if (_idleStopping) break;
                        _idleDoneCancellationTokenSource = idleDoneTokenSource;
                    }
                    try
                    {
                        try
                        {
                            await idleClient.IdleAsync(idleDoneTokenSource.Token, cancellationToken).ConfigureAwait(false);
                        }
                        catch (ImapCommandException ex)
                        {
                            _logger.Information(ex, "{AccountName} rejected IDLE despite advertising it. Updates will use scheduled synchronization.", Account.Name);
                            return;
                        }
                        if (DateTime.UtcNow - started >= TimeSpan.FromMinutes(1)) reconnectAttempt = 0;
                    }
                    finally
                    {
                        lock (_idleDoneLock) _idleDoneCancellationTokenSource = null;
                    }
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
            catch (AccountAttentionRequiredException)
            {
                _logger.Information("Stopped IDLE loop for {AccountName}: the account needs attention.", Account.Name);
                return;
            }
            catch (Exception ex) when (ex is MailKit.Security.AuthenticationException or MailKit.Security.SaslException)
            {
                // Rejected credentials cannot recover by reconnecting. Record the attention so every
                // other path stops too; the loop restarts after the user fixes the account.
                _logger.Warning(ex, "IDLE sign-in was rejected for {AccountName}. Stopping the IDLE loop.", Account.Name);
                await ReportIdleAuthenticationFailureAsync(ex).ConfigureAwait(false);
                return;
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
                    inboxFolder.MessageFlagsChanged -= IdleInboxChanged;
                    inboxFolder.MessageExpunged -= IdleInboxChanged;
                    inboxFolder.MessagesVanished -= IdleInboxChanged;
                }
                // This socket is retired; no UNSELECT or LOGOUT is needed during cleanup.
                _clientPool.ReleaseIdleClient(isFaulted: shouldReconnect);
            }

            if (cancellationToken.IsCancellationRequested || IsDisposing || _idleStopping) break;

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

    private async Task ReportIdleAuthenticationFailureAsync(Exception exception)
    {
        try
        {
            await _errorHandlerFactory.HandleErrorAsync(new SynchronizerErrorContext
            {
                Account = Account,
                ErrorMessage = exception.Message,
                Exception = exception,
                OperationType = "ImapIdle"
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to record IDLE sign-in failure for {AccountName}.", Account.Name);
        }
    }

    private static TimeSpan GetIdleReconnectDelay(int attempt)
    {
        var backoffSeconds = Math.Min(300, Math.Pow(2, Math.Min(attempt, 9)));
        int jitterMs;

        lock (IdleReconnectJitter)
        {
            jitterMs = IdleReconnectJitter.Next(250, 1250);
        }

        return TimeSpan.FromSeconds(backoffSeconds) + TimeSpan.FromMilliseconds(jitterMs);
    }

    private void RequestIdleChangeSynchronization()
    {
        lock (_idleDebounceLock)
        {
            if (_idleStopping || IsDisposing || _idleLoopCancellationTokenSource == null) return;
            _idleChangePending = true;
            _idleNotificationTask ??= Task.Run(FlushIdleChangesAsync);
        }
    }

    private async Task FlushIdleChangesAsync()
    {
        try
        {
            while (true)
            {
                TimeSpan delay;
                lock (_idleDebounceLock)
                {
                    if (!_idleChangePending || _idleStopping || IsDisposing) return;
                    delay = _idleSyncDebounceWindow - (DateTime.UtcNow - _lastIdleSyncRequestUtc);
                }
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _idleLoopCancellationTokenSource.Token).ConfigureAwait(false);
                lock (_idleDebounceLock)
                {
                    if (_idleStopping || IsDisposing) return;
                    _idleChangePending = false;
                    _lastIdleSyncRequestUtc = DateTime.UtcNow;
                }
                WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
                {
                    AccountId = Account.Id,
                    Type = MailSynchronizationType.IMAPIdle
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Warning(ex, "Failed to request IDLE synchronization for {AccountName}.", Account.Name); }
        finally
        {
            lock (_idleDebounceLock)
            {
                _idleNotificationTask = null;
                if (_idleChangePending && !_idleStopping && !IsDisposing)
                    _idleNotificationTask = Task.Run(FlushIdleChangesAsync);
            }
        }
    }

    private void IdleInboxCountChanged(object sender, EventArgs e)
    {
        if (sender is not IMailFolder inboxFolder)
            return;

        var currentCount = inboxFolder.Count;
        var previousCount = _lastIdleInboxCount;
        _lastIdleInboxCount = currentCount;

        if (currentCount != previousCount)
        {
            RequestIdleChangeSynchronization();
        }
    }

    protected override void OnIdleSynchronizationDeferred() => RequestIdleChangeSynchronization();

    private void IdleInboxChanged(object sender, EventArgs e) => RequestIdleChangeSynchronization();

    public async Task StopIdleClientAsync()
    {
        await _idleLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _idleStopping = true;
            lock (_idleDoneLock)
            {
                // MailKit requires DONE before canceling the command token on TLS streams.
                if (_idleDoneCancellationTokenSource != null)
                {
                    _idleDoneCancellationTokenSource.Cancel();
                    _idleLoopCancellationTokenSource?.CancelAfter(TimeSpan.FromSeconds(5));
                }
                else _idleLoopCancellationTokenSource?.Cancel();
            }
            try
            {
                if (_idleLoopTask != null) await _idleLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _idleLoopCancellationTokenSource?.Cancel();
                Task notifications;
                lock (_idleDebounceLock) notifications = _idleNotificationTask;
                if (notifications != null) await notifications.ConfigureAwait(false);
                _idleLoopCancellationTokenSource?.Dispose();
                _idleLoopCancellationTokenSource = null;
                _idleLoopTask = null;
                _idleChangePending = false;
            }
        }
        finally { _idleLifecycle.Release(); }
    }

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync().ConfigureAwait(false);
        try { await StopIdleClientAsync().ConfigureAwait(false); }
        finally { await _clientPool.DisposeAsync().ConfigureAwait(false); }
    }

    public Task PreWarmClientPoolAsync() => _clientPool.PreWarmPoolAsync();

    public async Task SuspendNetworkAccessAsync()
    {
        await StopIdleClientAsync().ConfigureAwait(false);
        _clientPool.RetireAvailableConnections();
    }
}



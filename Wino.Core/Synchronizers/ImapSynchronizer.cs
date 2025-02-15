using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MailKit;
using MailKit.Net.Imap;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Integration;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Services.Extensions;

namespace Wino.Core.Synchronizers.Mail;

public class ImapSynchronizer : WinoSynchronizer<ImapRequest, ImapMessageCreationPackage, object>, IImapSynchronizer
{
    [Obsolete("N/A")]
    public override uint BatchModificationSize => 1000;
    public override uint InitialMessageDownloadCountPerFolder => 500;

    #region Idle Implementation

    private CancellationTokenSource idleCancellationTokenSource;
    private CancellationTokenSource idleDoneTokenSource;

    #endregion

    private readonly ILogger _logger = Log.ForContext<ImapSynchronizer>();
    private readonly ImapClientPool _clientPool;
    private readonly IImapChangeProcessor _imapChangeProcessor;
    private readonly IImapSynchronizationStrategyProvider _imapSynchronizationStrategyProvider;
    private readonly IApplicationConfiguration _applicationConfiguration;

    public ImapSynchronizer(MailAccount account,
                            IImapChangeProcessor imapChangeProcessor,
                            IImapSynchronizationStrategyProvider imapSynchronizationStrategyProvider,
                            IApplicationConfiguration applicationConfiguration) : base(account)
    {
        // Create client pool with account protocol log.
        _imapChangeProcessor = imapChangeProcessor;
        _imapSynchronizationStrategyProvider = imapSynchronizationStrategyProvider;
        _applicationConfiguration = applicationConfiguration;

        var protocolLogStream = CreateAccountProtocolLogFileStream();
        var poolOptions = ImapClientPoolOptions.CreateDefault(Account.ServerInformation, protocolLogStream);

        _clientPool = new ImapClientPool(poolOptions);
    }

    private Stream CreateAccountProtocolLogFileStream()
    {
        if (Account == null) throw new ArgumentNullException(nameof(Account));

        var logFile = Path.Combine(_applicationConfiguration.ApplicationDataFolderPath, $"Protocol_{Account.Address}_{Account.Id}.log");

        // Each session should start a new log.
        if (File.Exists(logFile)) File.Delete(logFile);

        return new FileStream(logFile, FileMode.CreateNew);
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
        return CreateTaskBundle(async (client, item) =>
        {
            var sourceFolder = await client.GetFolderAsync(item.FromFolder.RemoteFolderId);
            var destinationFolder = await client.GetFolderAsync(item.ToFolder.RemoteFolderId);

            // Only opening source folder is enough.
            await sourceFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            await sourceFolder.MoveToAsync(GetUniqueId(item.Item.Id), destinationFolder).ConfigureAwait(false);
            await sourceFolder.CloseAsync().ConfigureAwait(false);
        }, requests);
    }

    public override List<IRequestBundle<ImapRequest>> ChangeFlag(BatchChangeFlagRequest requests)
    {
        return CreateTaskBundle(async (client, item) =>
        {
            var folder = item.Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId);

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            await remoteFolder.StoreAsync(GetUniqueId(item.Item.Id), new StoreFlagsRequest(item.Item.IsFlagged ? StoreAction.Add : StoreAction.Remove, MessageFlags.Flagged) { Silent = true }).ConfigureAwait(false);
            await remoteFolder.CloseAsync().ConfigureAwait(false);
        }, requests);
    }

    public override List<IRequestBundle<ImapRequest>> Delete(BatchDeleteRequest requests)
    {
        return CreateTaskBundle(async (client, request) =>
        {
            var folder = request.Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId).ConfigureAwait(false);

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            await remoteFolder.StoreAsync(GetUniqueId(request.Item.Id), new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }).ConfigureAwait(false);
            await remoteFolder.ExpungeAsync().ConfigureAwait(false);
            await remoteFolder.CloseAsync().ConfigureAwait(false);
        }, requests);
    }

    public override List<IRequestBundle<ImapRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        return CreateTaskBundle(async (client, request) =>
        {
            var folder = request.Item.AssignedFolder;
            var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId);

            await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
            await remoteFolder.StoreAsync(GetUniqueId(request.Item.Id), new StoreFlagsRequest(request.IsRead ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true }).ConfigureAwait(false);
            await remoteFolder.CloseAsync().ConfigureAwait(false);
        }, requests);
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

            // TODO: Transfer progress implementation as popup in the UI.
            await smtpClient.SendAsync(singleRequest.Mime, default);
            await smtpClient.DisconnectAsync(true);

            // SMTP sent the message, but we need to remove it from the Draft folder.
            var draftFolder = singleRequest.MailItem.AssignedFolder;

            var folder = await client.GetFolderAsync(draftFolder.RemoteFolderId);

            await folder.OpenAsync(FolderAccess.ReadWrite);

            var notUpdatedIds = await folder.StoreAsync(new UniqueId(MailkitClientExtensions.ResolveUid(singleRequest.MailItem.Id)), new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });

            await folder.ExpungeAsync();
            await folder.CloseAsync();

            // Check whether we need to create a copy of the message to Sent folder.
            // This comes from the account preferences.

            if (singleRequest.AccountPreferences.ShouldAppendMessagesToSentFolder && singleRequest.SentFolder != null)
            {
                var sentFolder = await client.GetFolderAsync(singleRequest.SentFolder.RemoteFolderId);

                await sentFolder.OpenAsync(FolderAccess.ReadWrite);

                // Delete local Wino draft header. Otherwise mapping will be applied on re-sync.
                singleRequest.Mime.Headers.Remove(Domain.Constants.WinoLocalDraftHeader);

                await sentFolder.AppendAsync(singleRequest.Mime, MessageFlags.Seen);
                await sentFolder.CloseAsync();
            }
        }, request, request);
    }

    public override async Task DownloadMissingMimeMessageAsync(IMailItem mailItem,
                                                           ITransferProgress transferProgress = null,
                                                           CancellationToken cancellationToken = default)
    {
        var folder = mailItem.AssignedFolder;
        var remoteFolderId = folder.RemoteFolderId;

        var client = await _clientPool.GetClientAsync().ConfigureAwait(false);
        var remoteFolder = await client.GetFolderAsync(remoteFolderId, cancellationToken).ConfigureAwait(false);

        var uniqueId = new UniqueId(MailkitClientExtensions.ResolveUid(mailItem.Id));

        await remoteFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

        var message = await remoteFolder.GetMessageAsync(uniqueId, cancellationToken, transferProgress).ConfigureAwait(false);

        await _imapChangeProcessor.SaveMimeFileAsync(mailItem.FileId, message, Account.Id).ConfigureAwait(false);
        await remoteFolder.CloseAsync(false, cancellationToken).ConfigureAwait(false);

        _clientPool.Release(client);
    }

    public override List<IRequestBundle<ImapRequest>> RenameFolder(RenameFolderRequest request)
    {
        return CreateSingleTaskBundle(async (client, item) =>
        {
            var folder = await client.GetFolderAsync(request.Folder.RemoteFolderId).ConfigureAwait(false);
            await folder.RenameAsync(folder.ParentFolder, request.NewFolderName).ConfigureAwait(false);
        }, request, request);
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

        if (message.MimeMessage != null &&
            message.MimeMessage.Headers.Contains(Domain.Constants.WinoLocalDraftHeader) &&
            Guid.TryParse(message.MimeMessage.Headers[Domain.Constants.WinoLocalDraftHeader], out Guid localDraftCopyUniqueId))
        {
            // This message belongs to existing local draft copy.
            // We don't need to create a new mail copy for this message, just update the existing one.

            bool isMappingSuccessful = await _imapChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopyUniqueId, mailCopy.Id, mailCopy.DraftId, mailCopy.ThreadId);

            if (isMappingSuccessful) return null;

            // Local copy doesn't exists. Continue execution to insert mail copy.
        }

        var package = new NewMailItemPackage(mailCopy, message.MimeMessage, assignedFolder.RemoteFolderId);

        return
        [
            package
        ];
    }

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();

        _logger.Information("Internal synchronization started for {Name}", Account.Name);
        _logger.Information("Options: {Options}", options);

        PublishSynchronizationProgress(1);

        bool shouldDoFolderSync = options.Type == MailSynchronizationType.FullFolders || options.Type == MailSynchronizationType.FoldersOnly;

        if (shouldDoFolderSync)
        {
            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
        }

        if (options.Type != MailSynchronizationType.FoldersOnly)
        {
            var synchronizationFolders = await _imapChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);

            for (int i = 0; i < synchronizationFolders.Count; i++)
            {
                var folder = synchronizationFolders[i];
                var progress = (int)Math.Round((double)(i + 1) / synchronizationFolders.Count * 100);

                PublishSynchronizationProgress(progress);

                var folderDownloadedMessageIds = await SynchronizeFolderInternalAsync(folder, cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested) return MailSynchronizationResult.Canceled;

                if (folderDownloadedMessageIds != null)
                {
                    downloadedMessageIds.AddRange(folderDownloadedMessageIds);
                }
            }
        }

        PublishSynchronizationProgress(100);

        // Get all unread new downloaded items and return in the result.
        // This is primarily used in notifications.

        var unreadNewItems = await _imapChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);

        return MailSynchronizationResult.Completed(unreadNewItems);
    }

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<ImapRequest>> batchedRequests, CancellationToken cancellationToken = default)
    {
        // First apply the UI changes for each bundle.
        // This is important to reflect changes to the UI before the network call is done.

        foreach (var item in batchedRequests)
        {
            item.Request.ApplyUIChanges();
        }

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
                executorClient = await _clientPool.GetClientAsync();
            }
            catch (ImapClientPoolException)
            {
                // Client pool failed to get a client.
                // Requests may not be executed at this point.

                item.Request.RevertUIChanges();

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

            // TODO: Retry pattern.
            // TODO: Error handling.
            try
            {
                await item.NativeRequest.IntegratorTask(executorClient, item.Request).ConfigureAwait(false);
            }
            catch (Exception)
            {
                item.Request.RevertUIChanges();
                throw;
            }
            finally
            {
                _clientPool.Release(executorClient);
            }
        }
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

                // Check for NoSelect folders. These are not selectable folders.
                // TODO: With new MailKit version 'CanOpen' will be implemented for ease of use. Use that one.
                if (remoteFolder.Attributes.HasFlag(FolderAttributes.NoSelect))
                    continue;

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
                WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
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

    private async Task<IEnumerable<string>> SynchronizeFolderInternalAsync(MailItemFolder folder, CancellationToken cancellationToken = default)
    {
        if (!folder.IsSynchronizationEnabled) return default;

        IImapClient availableClient = null;

    retry:
        try
        {

            availableClient = await _clientPool.GetClientAsync().ConfigureAwait(false);

            var strategy = _imapSynchronizationStrategyProvider.GetSynchronizationStrategy(availableClient);
            return await strategy.HandleSynchronizationAsync(availableClient, folder, this, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _clientPool.Release(availableClient, false);

            goto retry;
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellations.
        }
        catch (Exception)
        {

        }
        finally
        {
            _clientPool.Release(availableClient, false);
        }

        return new List<string>();
    }

    /// <summary>
    /// Whether the local folder should be updated with the remote folder.
    /// IMAP only compares folder name for now.
    /// </summary>
    /// <param name="remoteFolder">Remote folder</param>
    /// <param name="localFolder">Local folder.</param>
    public bool ShouldUpdateFolder(IMailFolder remoteFolder, MailItemFolder localFolder)
        => !localFolder.FolderName.Equals(remoteFolder.Name, StringComparison.OrdinalIgnoreCase);

    protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task StartIdleClientAsync()
    {
        IImapClient idleClient = null;
        IMailFolder inboxFolder = null;

        bool? reconnect = null;

        try
        {
            var client = await _clientPool.GetClientAsync().ConfigureAwait(false);

            if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
            {
                Log.Debug($"{Account.Name} does not support Idle command. Ignored.");
                return;
            }

            if (client.Inbox == null)
            {
                Log.Warning($"{Account.Name} does not have an Inbox folder for idle client to track. Ignored.");
                return;
            }

            // Setup idle client.
            idleClient = client;

            idleDoneTokenSource ??= new CancellationTokenSource();
            idleCancellationTokenSource ??= new CancellationTokenSource();

            inboxFolder = client.Inbox;

            await inboxFolder.OpenAsync(FolderAccess.ReadOnly, idleCancellationTokenSource.Token);

            inboxFolder.CountChanged += IdleNotificationTriggered;
            inboxFolder.MessageFlagsChanged += IdleNotificationTriggered;
            inboxFolder.MessageExpunged += IdleNotificationTriggered;
            inboxFolder.MessagesVanished += IdleNotificationTriggered;

            Log.Debug("Starting an idle client for {Name}", Account.Name);

            await client.IdleAsync(idleDoneTokenSource.Token, idleCancellationTokenSource.Token);
        }
        catch (ImapProtocolException protocolException)
        {
            Log.Warning(protocolException, "Idle client received protocol exception.");
            reconnect = true;
        }
        catch (IOException ioException)
        {
            Log.Warning(ioException, "Idle client received IO exception.");
            reconnect = true;
        }
        catch (OperationCanceledException)
        {
            reconnect = !IsDisposing;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Idle client failed to start.");
            reconnect = false;
        }
        finally
        {
            if (inboxFolder != null)
            {
                inboxFolder.CountChanged -= IdleNotificationTriggered;
                inboxFolder.MessageFlagsChanged -= IdleNotificationTriggered;
                inboxFolder.MessageExpunged -= IdleNotificationTriggered;
                inboxFolder.MessagesVanished -= IdleNotificationTriggered;
            }

            if (idleDoneTokenSource != null)
            {
                idleDoneTokenSource.Dispose();
                idleDoneTokenSource = null;
            }

            if (idleClient != null)
            {
                // Killing the client is not necessary. We can re-use it later.
                _clientPool.Release(idleClient, destroyClient: false);

                idleClient = null;
            }

            if (reconnect == true)
            {
                Log.Information("Idle client is reconnecting.");

                _ = StartIdleClientAsync();
            }
            else if (reconnect == false)
            {
                Log.Information("Finalized idle client.");
            }
        }
    }

    private void RequestIdleChangeSynchronization()
    {
        Debug.WriteLine("Detected idle change.");

        // We don't really need to act on the count change in detail.
        // Our synchronization should be enough to handle the changes with on-demand sync.
        // We can just trigger a sync here IMAPIdle type.

        var options = new MailSynchronizationOptions()
        {
            AccountId = Account.Id,
            Type = MailSynchronizationType.IMAPIdle
        };

        WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(options, SynchronizationSource.Client));
    }

    private void IdleNotificationTriggered(object sender, EventArgs e)
        => RequestIdleChangeSynchronization();

    public Task StopIdleClientAsync()
    {
        idleDoneTokenSource?.Cancel();
        idleCancellationTokenSource?.Cancel();

        return Task.CompletedTask;
    }

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync();
        await StopIdleClientAsync();

        // Make sure the client pool safely disconnects all ImapClients.
        _clientPool.Dispose();
    }
}

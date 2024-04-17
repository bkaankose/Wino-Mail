using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Integration;
using Wino.Core.Integration.Processors;
using Wino.Core.Mime;
using Wino.Core.Requests;
using Wino.Core.Requests.Bundles;

namespace Wino.Core.Synchronizers
{
    public class ImapSynchronizer : BaseSynchronizer<ImapRequest, ImapMessageCreationPackage>
    {
        private CancellationTokenSource idleDoneToken;
        private CancellationTokenSource cancelInboxListeningToken = new CancellationTokenSource();

        private IMailFolder inboxFolder;

        private readonly ILogger _logger = Log.ForContext<ImapSynchronizer>();
        private readonly ImapClientPool _clientPool;
        private readonly IDefaultChangeProcessor _imapChangeProcessor;

        // Minimum summary items to Fetch for mail synchronization from IMAP.
        private readonly MessageSummaryItems mailSynchronizationFlags =
            MessageSummaryItems.Flags |
            MessageSummaryItems.UniqueId |
            MessageSummaryItems.ThreadId |
            MessageSummaryItems.EmailId |
            MessageSummaryItems.Headers |
            MessageSummaryItems.PreviewText |
            MessageSummaryItems.GMailThreadId |
            MessageSummaryItems.References |
            MessageSummaryItems.ModSeq;

        /// <summary>
        /// Timer that keeps the <see cref="InboxClient"/> alive for the lifetime of the pool.
        /// Sends NOOP command to the server periodically.
        /// </summary>
        private Timer _noOpTimer;

        /// <summary>
        /// ImapClient that keeps the Inbox folder opened all the time for listening notifications.
        /// </summary>
        private ImapClient _inboxIdleClient;

        public override uint BatchModificationSize => 1000;
        public override uint InitialMessageDownloadCountPerFolder => 500;

        public ImapSynchronizer(MailAccount account, IDefaultChangeProcessor imapChangeProcessor) : base(account)
        {
            _clientPool = new ImapClientPool(Account.ServerInformation);

            idleDoneToken = new CancellationTokenSource();
            _imapChangeProcessor = imapChangeProcessor;
        }

        // TODO
        // private async void NoOpTimerTriggered(object state) => await AwaitInboxIdleAsync();

        private async Task AwaitInboxIdleAsync()
        {
            if (_inboxIdleClient == null)
            {
                _logger.Warning("InboxClient is null. Cannot send NOOP command.");
                return;
            }

            await _clientPool.EnsureConnectedAsync(_inboxIdleClient);
            await _clientPool.EnsureAuthenticatedAsync(_inboxIdleClient);

            try
            {
                if (inboxFolder == null)
                {
                    inboxFolder = _inboxIdleClient.Inbox;
                    await inboxFolder.OpenAsync(FolderAccess.ReadOnly, cancelInboxListeningToken.Token);
                }

                idleDoneToken = new CancellationTokenSource();

                await _inboxIdleClient.IdleAsync(idleDoneToken.Token, cancelInboxListeningToken.Token);
            }
            finally
            {
                idleDoneToken.Dispose();
                idleDoneToken = null;
            }
        }

        private async Task StopInboxListeningAsync()
        {
            if (inboxFolder != null)
            {
                inboxFolder.CountChanged -= InboxFolderCountChanged;
                inboxFolder.MessageExpunged -= InboxFolderMessageExpunged;
                inboxFolder.MessageFlagsChanged -= InboxFolderMessageFlagsChanged;
            }

            if (_noOpTimer != null)
            {
                _noOpTimer.Dispose();
                _noOpTimer = null;
            }

            if (idleDoneToken != null)
            {
                idleDoneToken.Cancel();
                idleDoneToken.Dispose();
                idleDoneToken = null;
            }

            if (_inboxIdleClient != null)
            {
                await _inboxIdleClient.DisconnectAsync(true);
                _inboxIdleClient.Dispose();
                _inboxIdleClient = null;
            }
        }

        /// <summary>
        /// Tries to connect & authenticate with the given credentials.
        /// Prepares synchronizer for active listening of Inbox folder.
        /// </summary>
        public async Task StartInboxListeningAsync()
        {
            _inboxIdleClient = await _clientPool.GetClientAsync();

            // Run it every 8 minutes after 1 minute delay.
            // _noOpTimer = new Timer(NoOpTimerTriggered, null, 60000, 8 * 60 * 1000);

            await _clientPool.EnsureConnectedAsync(_inboxIdleClient);
            await _clientPool.EnsureAuthenticatedAsync(_inboxIdleClient);

            if (!_inboxIdleClient.Capabilities.HasFlag(ImapCapabilities.Idle))
            {
                _logger.Information("Imap server does not support IDLE command. Listening live changes is not supported for {Name}", Account.Name);
                return;
            }

            inboxFolder = _inboxIdleClient.Inbox;

            if (inboxFolder == null)
            {
                _logger.Information("Inbox folder is null. Cannot listen for changes.");
                return;
            }

            inboxFolder.CountChanged += InboxFolderCountChanged;
            inboxFolder.MessageExpunged += InboxFolderMessageExpunged;
            inboxFolder.MessageFlagsChanged += InboxFolderMessageFlagsChanged;

            while (!cancelInboxListeningToken.IsCancellationRequested)
            {
                await AwaitInboxIdleAsync();
            }

            await StopInboxListeningAsync();
        }

        private void InboxFolderMessageFlagsChanged(object sender, MessageFlagsChangedEventArgs e)
        {
            Console.WriteLine("Flags have changed for message #{0} ({1}).", e.Index, e.Flags);
        }

        private void InboxFolderMessageExpunged(object sender, MessageEventArgs e)
        {
            _logger.Information("Inbox folder message expunged");
        }

        private void InboxFolderCountChanged(object sender, EventArgs e)
        {
            _logger.Information("Inbox folder count changed.");
        }

        /// <summary>
        /// Parses List of string of mail copy ids and return valid uIds.
        /// Follow the rules for creating arbitrary unique id for mail copies.
        /// </summary>
        private UniqueIdSet GetUniqueIds(IEnumerable<string> mailCopyIds)
            => new(mailCopyIds.Select(a => new UniqueId(MailkitClientExtensions.ResolveUid(a))));

        #region Mail Integrations

        // Items are grouped before being passed to this method.
        // Meaning that all items will come from and to the same folder.
        // It's fine to assume that here.

        public override IEnumerable<IRequestBundle<ImapRequest>> Move(BatchMoveRequest request)
        {
            return CreateTaskBundle(async (ImapClient client) =>
            {
                var uniqueIds = GetUniqueIds(request.Items.Select(a => a.Item.Id));

                var sourceFolder = await client.GetFolderAsync(request.FromFolder.RemoteFolderId);
                var destinationFolder = await client.GetFolderAsync(request.ToFolder.RemoteFolderId);

                // Only opening source folder is enough.
                await sourceFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
                await sourceFolder.MoveToAsync(uniqueIds, destinationFolder).ConfigureAwait(false);
                await sourceFolder.CloseAsync().ConfigureAwait(false);
            }, request);
        }

        public override IEnumerable<IRequestBundle<ImapRequest>> ChangeFlag(BatchChangeFlagRequest request)
        {
            return CreateTaskBundle(async (ImapClient client) =>
            {
                var folder = request.Items.First().Item.AssignedFolder;
                var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId);
                var uniqueIds = GetUniqueIds(request.Items.Select(a => a.Item.Id));

                await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
                await remoteFolder.StoreAsync(uniqueIds, new StoreFlagsRequest(request.IsFlagged ? StoreAction.Add : StoreAction.Remove, MessageFlags.Flagged) { Silent = true }).ConfigureAwait(false);
                await remoteFolder.CloseAsync().ConfigureAwait(false);
            }, request);
        }

        public override IEnumerable<IRequestBundle<ImapRequest>> Delete(BatchDeleteRequest request)
        {
            return CreateTaskBundle(async (ImapClient client) =>
            {
                var folder = request.Items.First().Item.AssignedFolder;
                var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId).ConfigureAwait(false);
                var uniqueIds = GetUniqueIds(request.Items.Select(a => a.Item.Id));

                await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
                await remoteFolder.StoreAsync(uniqueIds, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }).ConfigureAwait(false);
                await remoteFolder.ExpungeAsync().ConfigureAwait(false);
                await remoteFolder.CloseAsync().ConfigureAwait(false);
            }, request);
        }

        public override IEnumerable<IRequestBundle<ImapRequest>> MarkRead(BatchMarkReadRequest request)
        {
            return CreateTaskBundle(async (ImapClient client) =>
            {
                var folder = request.Items.First().Item.AssignedFolder;
                var remoteFolder = await client.GetFolderAsync(folder.RemoteFolderId);
                var uniqueIds = GetUniqueIds(request.Items.Select(a => a.Item.Id));

                await remoteFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
                await remoteFolder.StoreAsync(uniqueIds, new StoreFlagsRequest(request.IsRead ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true }).ConfigureAwait(false);
                await remoteFolder.CloseAsync().ConfigureAwait(false);
            }, request);
        }

        public override IEnumerable<IRequestBundle<ImapRequest>> CreateDraft(BatchCreateDraftRequest request)
        {
            return CreateTaskBundle(async (ImapClient client) =>
            {

                var remoteDraftFolder = await client.GetFolderAsync(request.DraftPreperationRequest.CreatedLocalDraftCopy.AssignedFolder.RemoteFolderId).ConfigureAwait(false);

                await remoteDraftFolder.OpenAsync(FolderAccess.ReadWrite).ConfigureAwait(false);
                await remoteDraftFolder.AppendAsync(request.DraftPreperationRequest.CreatedLocalDraftMimeMessage, MessageFlags.Draft).ConfigureAwait(false);
                await remoteDraftFolder.CloseAsync().ConfigureAwait(false);
            }, request);
        }

        public override IEnumerable<IRequestBundle<ImapRequest>> SendDraft(BatchSendDraftRequestRequest request)
        {
            return CreateTaskBundle(async (ImapClient client) =>
            {
                // Batch sending is not supported. It will always be a single request therefore no need for a loop here.

                var singleRequest = request.Request;

                singleRequest.Mime.Prepare(EncodingConstraint.None);

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
            }, request);
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

        #endregion

        public override async Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(ImapMessageCreationPackage message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
        {
            var imapFolder = message.MailFolder;
            var summary = message.MessageSummary;

            var mimeMessage = await imapFolder.GetMessageAsync(summary.UniqueId, cancellationToken).ConfigureAwait(false);
            var mailCopy = summary.GetMailDetails(assignedFolder, mimeMessage);

            // Draft folder message updates must be updated as IsDraft.
            // I couldn't find it in MimeMessage...

            mailCopy.IsDraft = assignedFolder.SpecialFolderType == SpecialFolderType.Draft;

            // Check draft mapping.
            // This is the same implementation as in the OutlookSynchronizer.

            if (mimeMessage.Headers.Contains(Domain.Constants.WinoLocalDraftHeader)
                && Guid.TryParse(mimeMessage.Headers[Domain.Constants.WinoLocalDraftHeader], out Guid localDraftCopyUniqueId))
            {
                // This message belongs to existing local draft copy.
                // We don't need to create a new mail copy for this message, just update the existing one.

                bool isMappingSuccessful = await _imapChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopyUniqueId, mailCopy.Id, mailCopy.DraftId, mailCopy.ThreadId);

                if (isMappingSuccessful) return null;

                // Local copy doesn't exists. Continue execution to insert mail copy.
            }

            var package = new NewMailItemPackage(mailCopy, mimeMessage, assignedFolder.RemoteFolderId);

            return
            [
                package
            ];
        }

        public override async Task<SynchronizationResult> SynchronizeInternalAsync(SynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            // options.Type = SynchronizationType.FoldersOnly;

            var downloadedMessageIds = new List<string>();

            _logger.Information("Internal synchronization started for {Name}", Account.Name);
            _logger.Information("Options: {Options}", options);

            options.ProgressListener?.AccountProgressUpdated(Account.Id, 1);

            // Only do folder sync for these types.
            // Opening folder and checking their UidValidity is slow.
            // Therefore this should be avoided as many times as possible.

            // This may create some inconsistencies, but nothing we can do...
            if (options.Type == SynchronizationType.FoldersOnly || options.Type == SynchronizationType.Full)
            {
                await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            }

            if (options.Type != SynchronizationType.FoldersOnly)
            {
                var synchronizationFolders = await _imapChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);

                for (int i = 0; i < synchronizationFolders.Count; i++)
                {
                    var folder = synchronizationFolders[i];
                    var progress = (int)Math.Round((double)(i + 1) / synchronizationFolders.Count * 100);

                    options.ProgressListener?.AccountProgressUpdated(Account.Id, progress);

                    var folderDownloadedMessageIds = await SynchronizeFolderInternalAsync(folder, cancellationToken).ConfigureAwait(false);
                    downloadedMessageIds.AddRange(folderDownloadedMessageIds);
                }
            }

            options.ProgressListener?.AccountProgressUpdated(Account.Id, 100);

            // Get all unread new downloaded items and return in the result.
            // This is primarily used in notifications.

            var unreadNewItems = await _imapChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);

            return SynchronizationResult.Completed(unreadNewItems);
        }

        public override async Task ExecuteNativeRequestsAsync(IEnumerable<IRequestBundle<ImapRequest>> batchedRequests, CancellationToken cancellationToken = default)
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

                ImapClient executorClient = null;

                try
                {
                    executorClient = await _clientPool.GetClientAsync();
                }
                catch (ImapClientPoolException)
                {
                    // Client pool failed to get a client.
                    // Requests may not be executed at this point.

                    item.Request.RevertUIChanges();

                    throw;
                }
                finally
                {
                    if (executorClient != null)
                    {
                        _clientPool.Release(executorClient);
                    }
                }

                // TODO: Retry pattern.
                // TODO: Error handling.
                try
                {
                    await item.NativeRequest.IntegratorTask(executorClient).ConfigureAwait(false);
                }
                catch (Exception)
                {

                    throw;
                }
                finally
                {
                    _clientPool.Release(executorClient);
                }
            }
        }

        // TODO: This can be optimized by starting checking the local folders UidValidtyNext first.
        // TODO: We need to determine deleted folders here. Also for that we need to start by local folders.
        // TODO: UpdateFolderStructureAsync
        private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken = default)
        {
            // https://www.rfc-editor.org/rfc/rfc4549#section-1.1    

            var _synchronizationClient = await _clientPool.GetClientAsync();

            var nameSpace = _synchronizationClient.PersonalNamespaces[0];

            var folders = (await _synchronizationClient.GetFoldersAsync(nameSpace, cancellationToken: cancellationToken)).ToList();

            // Special folders

            bool isSpecialFoldersSupported = _synchronizationClient.Capabilities.HasFlag(ImapCapabilities.SpecialUse) || _synchronizationClient.Capabilities.HasFlag(ImapCapabilities.XList);

            // Inbox is always available.
            IMailFolder inbox = _synchronizationClient.Inbox, drafts = null, junk = null, trash = null, sent = null, archive = null, important = null, starred = null;

            if (isSpecialFoldersSupported)
            {
                drafts = _synchronizationClient.GetFolder(SpecialFolder.Drafts);
                junk = _synchronizationClient.GetFolder(SpecialFolder.Junk);
                trash = _synchronizationClient.GetFolder(SpecialFolder.Trash);
                sent = _synchronizationClient.GetFolder(SpecialFolder.Sent);
                archive = _synchronizationClient.GetFolder(SpecialFolder.Archive);
                important = _synchronizationClient.GetFolder(SpecialFolder.Important);
                starred = _synchronizationClient.GetFolder(SpecialFolder.Flagged);
            }

            var mailItemFolders = new List<MailItemFolder>();

            // Sometimes Inbox is the root namespace. We need to check for that.
            if (inbox != null && !folders.Contains(inbox))
                folders.Add(inbox);

            foreach (var item in folders)
            {
                // Namespaces are not needed as folders.
                // Non-existed folders don't need to be synchronized.

                if ((item.IsNamespace && !item.Attributes.HasFlag(FolderAttributes.Inbox)) || !item.Exists)
                    continue;

                // Try to synchronize all folders.

                try
                {
                    var localFolder = item.GetLocalFolder();

                    if (item == inbox)
                        localFolder.SpecialFolderType = SpecialFolderType.Inbox;

                    if (isSpecialFoldersSupported)
                    {
                        if (item == drafts)
                            localFolder.SpecialFolderType = SpecialFolderType.Draft;
                        else if (item == junk)
                            localFolder.SpecialFolderType = SpecialFolderType.Junk;
                        else if (item == trash)
                            localFolder.SpecialFolderType = SpecialFolderType.Deleted;
                        else if (item == sent)
                            localFolder.SpecialFolderType = SpecialFolderType.Sent;
                        else if (item == archive)
                            localFolder.SpecialFolderType = SpecialFolderType.Archive;
                        else if (item == important)
                            localFolder.SpecialFolderType = SpecialFolderType.Important;
                        else if (item == starred)
                            localFolder.SpecialFolderType = SpecialFolderType.Starred;
                    }

                    if (localFolder.SpecialFolderType != SpecialFolderType.Other)
                    {
                        localFolder.IsSystemFolder = true;
                        localFolder.IsSticky = true;
                        localFolder.IsSynchronizationEnabled = true;
                    }
                    else
                    {
                        localFolder.IsSynchronizationEnabled = false;
                    }

                    // By default, all special folders update unread count in the UI except Trash.
                    localFolder.ShowUnreadCount = localFolder.SpecialFolderType != SpecialFolderType.Deleted || localFolder.SpecialFolderType != SpecialFolderType.Other;

                    localFolder.MailAccountId = Account.Id;

                    // Sometimes sub folders are parented under Inbox.
                    // Even though this makes sense in server level, in the client it sucks.
                    // That will make sub folders to be parented under Inbox in the client.
                    // Instead, we will mark them as non-parented folders.
                    // This is better. Model allows personalized folder structure anyways
                    // even though we don't have the page/control to adjust it.

                    if (item.ParentFolder == _synchronizationClient.Inbox)
                        localFolder.ParentRemoteFolderId = string.Empty;

                    // Set UidValidity for cache expiration.
                    // Folder must be opened for this.

                    await item.OpenAsync(FolderAccess.ReadOnly);

                    localFolder.UidValidity = item.UidValidity;

                    await item.CloseAsync();

                    mailItemFolders.Add(localFolder);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Folder with name '{item.Name}' failed to be synchronized.");
                }
            }

            await _imapChangeProcessor.UpdateFolderStructureAsync(Account.Id, mailItemFolders);
        }

        private async Task<IEnumerable<string>> SynchronizeFolderInternalAsync(MailItemFolder folder, CancellationToken cancellationToken = default)
        {
            if (!folder.IsSynchronizationEnabled) return default;

            var downloadedMessageIds = new List<string>();

            // STEP1: Ask for flag changes for older mails.
            // STEP2: Get new mail changes.
            // https://www.rfc-editor.org/rfc/rfc4549 - Section 4.3

            var _synchronizationClient = await _clientPool.GetClientAsync();

            IMailFolder imapFolder = null;

            var knownMailIds = new UniqueIdSet();
            var locallyKnownMailUids = await _imapChangeProcessor.GetKnownUidsForFolderAsync(folder.Id);
            knownMailIds.AddRange(locallyKnownMailUids.Select(a => new UniqueId(a)));

            var highestUniqueId = Math.Max(0, locallyKnownMailUids.Count == 0 ? 0 : locallyKnownMailUids.Max());

            var missingMailIds = new UniqueIdSet();

            var uidValidity = folder.UidValidity;
            var highestModeSeq = folder.HighestModeSeq;

            var logger = Log.ForContext("FolderName", folder.FolderName);

            logger.Verbose("HighestModeSeq: {HighestModeSeq}, HighestUniqueId: {HighestUniqueId}, UIDValidity: {UIDValidity}", highestModeSeq, highestUniqueId, uidValidity);

            // Event handlers are placed here to handle existing MailItemFolder and IIMailFolder from MailKit.
            // MailKit doesn't expose folder data when these events are emitted.

            // Use local folder's UidValidty because cache might've been expired for remote IMAP folder.
            // That will make our mail copy id invalid.

            EventHandler<MessagesVanishedEventArgs> MessageVanishedHandler = async (s, e) =>
            {
                if (imapFolder == null) return;

                foreach (var uniqueId in e.UniqueIds)
                {
                    var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, uniqueId.Id);

                    await _imapChangeProcessor.DeleteMailAsync(Account.Id, localMailCopyId);
                }
            };

            EventHandler<MessageFlagsChangedEventArgs> MessageFlagsChangedHandler = async (s, e) =>
            {
                if (imapFolder == null) return;

                var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, e.UniqueId.Value.Id);

                var isFlagged = MailkitClientExtensions.GetIsFlagged(e.Flags);
                var isRead = MailkitClientExtensions.GetIsRead(e.Flags);

                await _imapChangeProcessor.ChangeMailReadStatusAsync(localMailCopyId, isRead);
                await _imapChangeProcessor.ChangeFlagStatusAsync(localMailCopyId, isFlagged);
            };

            EventHandler<MessageEventArgs> MessageExpungedHandler = async (s, e) =>
            {
                if (imapFolder == null) return;
                if (e.UniqueId == null) return;

                var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, e.UniqueId.Value.Id);
                await _imapChangeProcessor.DeleteMailAsync(Account.Id, localMailCopyId);
            };

            try
            {
                imapFolder = await _synchronizationClient.GetFolderAsync(folder.RemoteFolderId, cancellationToken);

                imapFolder.MessageFlagsChanged += MessageFlagsChangedHandler;

                // TODO: Bug: Enabling quick re-sync actually doesn't enable it.

                var qsyncEnabled = false; // _synchronizationClient.Capabilities.HasFlag(ImapCapabilities.QuickResync);
                var condStoreEnabled = _synchronizationClient.Capabilities.HasFlag(ImapCapabilities.CondStore);

                if (qsyncEnabled)
                {

                    imapFolder.MessagesVanished += MessageVanishedHandler;

                    await imapFolder.OpenAsync(FolderAccess.ReadWrite, uidValidity, (ulong)highestModeSeq, knownMailIds, cancellationToken);

                    // Check the folder validity.
                    // We'll delete our existing cache if it's not.

                    // Get all messages after the last successful synchronization date.
                    // This is fine for Wino synchronization because we're not really looking to
                    // synchronize all folder.

                    var allMessageIds = await imapFolder.SearchAsync(SearchQuery.All, cancellationToken);

                    if (uidValidity != imapFolder.UidValidity)
                    {
                        // TODO: Cache is invalid. Delete all local cache.
                        //await ChangeProcessor.FolderService.ClearImapFolderCacheAsync(folder.Id);

                        folder.UidValidity = imapFolder.UidValidity;
                        missingMailIds.AddRange(allMessageIds);
                    }
                    else
                    {
                        // Cache is valid.
                        // Add missing mails only.

                        missingMailIds.AddRange(allMessageIds.Except(knownMailIds).Where(a => a.Id > highestUniqueId));
                    }
                }
                else
                {
                    // QSYNC extension is not enabled for the server.
                    // We rely on ConditionalStore.

                    imapFolder.MessageExpunged += MessageExpungedHandler;
                    await imapFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

                    // Get all messages after the last succesful synchronization date.
                    // This is fine for Wino synchronization because we're not really looking to
                    // synchronize all folder.

                    var allMessageIds = await imapFolder.SearchAsync(SearchQuery.All, cancellationToken);

                    if (uidValidity != imapFolder.UidValidity)
                    {
                        // TODO: Cache is invalid. Delete all local cache.
                        // await ChangeProcessor.FolderService.ClearImapFolderCacheAsync(folder.Id);

                        folder.UidValidity = imapFolder.UidValidity;
                        missingMailIds.AddRange(allMessageIds);
                    }
                    else
                    {
                        // Cache is valid.

                        var purgedMessages = knownMailIds.Except(allMessageIds);

                        foreach (var purgedMessage in purgedMessages)
                        {
                            var mailId = MailkitClientExtensions.CreateUid(folder.Id, purgedMessage.Id);

                            await _imapChangeProcessor.DeleteMailAsync(Account.Id, mailId);
                        }

                        IList<IMessageSummary> changed;

                        if (knownMailIds.Count > 0)
                        {
                            // CONDSTORE enabled. Fetch items with highest mode seq for known items
                            // to track flag changes. Otherwise just get changes without the mode seq.

                            if (condStoreEnabled)
                                changed = await imapFolder.FetchAsync(knownMailIds, (ulong)highestModeSeq, MessageSummaryItems.Flags | MessageSummaryItems.ModSeq | MessageSummaryItems.UniqueId);
                            else
                                changed = await imapFolder.FetchAsync(knownMailIds, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId);

                            foreach (var changedItem in changed)
                            {
                                var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, changedItem.UniqueId.Id);

                                var isFlagged = MailkitClientExtensions.GetIsFlagged(changedItem.Flags);
                                var isRead = MailkitClientExtensions.GetIsRead(changedItem.Flags);

                                await _imapChangeProcessor.ChangeMailReadStatusAsync(localMailCopyId, isRead);
                                await _imapChangeProcessor.ChangeFlagStatusAsync(localMailCopyId, isFlagged);
                            }
                        }

                        // We're only interested in items that has highier known uid than we fetched before.
                        // Others are just older messages.

                        missingMailIds.AddRange(allMessageIds.Except(knownMailIds).Where(a => a.Id > highestUniqueId));
                    }
                }

                // Fetch completely missing new items in the end.

                // Limit check.
                //if (missingMailIds.Count > TAKE_COUNT)
                //{
                //    missingMailIds = new UniqueIdSet(missingMailIds.TakeLast(TAKE_COUNT));
                //}

                // In case of the high input, we'll batch them by 50 to reflect changes quickly.
                var batchedMissingMailIds = missingMailIds.Batch(50).Select(a => new UniqueIdSet(a, SortOrder.Descending));

                foreach (var batchMissingMailIds in batchedMissingMailIds)
                {
                    var summaries = await imapFolder.FetchAsync(batchMissingMailIds, mailSynchronizationFlags, cancellationToken);

                    foreach (var summary in summaries)
                    {
                        // We pass the opened folder and summary to retrieve raw MimeMessage.

                        var creationPackage = new ImapMessageCreationPackage(summary, imapFolder);
                        var createdMailPackages = await CreateNewMailPackagesAsync(creationPackage, folder, cancellationToken).ConfigureAwait(false);

                        // Local draft is mapped. We don't need to create a new mail copy.
                        if (createdMailPackages == null)
                            continue;

                        foreach (var mailPackage in createdMailPackages)
                        {
                            await _imapChangeProcessor.CreateMailAsync(Account.Id, mailPackage);
                        }
                    }
                }

                if (folder.HighestModeSeq != (long)imapFolder.HighestModSeq)
                {
                    folder.HighestModeSeq = (long)imapFolder.HighestModSeq;
                    await _imapChangeProcessor.InsertFolderAsync(folder);
                }

                // Update last synchronization identifier.
                // This will update last sync date for the folder.

                await _imapChangeProcessor.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, string.Empty).ConfigureAwait(false);

                return downloadedMessageIds;
            }
            catch (FolderNotFoundException)
            {
                await _imapChangeProcessor.DeleteFolderAsync(Account.Id, folder.RemoteFolderId);

                return default;
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                if (imapFolder != null)
                {
                    imapFolder.MessageFlagsChanged -= MessageFlagsChangedHandler;
                    imapFolder.MessageExpunged -= MessageExpungedHandler;
                    imapFolder.MessagesVanished -= MessageVanishedHandler;

                    if (imapFolder.IsOpen)
                        await imapFolder.CloseAsync();
                }

                _clientPool.Release(_synchronizationClient);
            }
        }
    }
}

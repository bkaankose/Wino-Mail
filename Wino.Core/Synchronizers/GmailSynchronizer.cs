using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Http;
using Google.Apis.Requests;
using Google.Apis.Services;
using MailKit;
using Microsoft.IdentityModel.Tokens;
using MimeKit;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Http;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests;

namespace Wino.Core.Synchronizers
{
    public class GmailSynchronizer : BaseSynchronizer<IClientServiceRequest, Message>, IHttpClientFactory
    {
        public override uint BatchModificationSize => 1000;
        public override uint InitialMessageDownloadCountPerFolder => 1200;

        // It's actually 100. But Gmail SDK has internal bug for Out of Memory exception.
        // https://github.com/googleapis/google-api-dotnet-client/issues/2603
        private const uint MaximumAllowedBatchRequestSize = 10;

        private readonly ConfigurableHttpClient _gmailHttpClient;
        private readonly GmailService _gmailService;
        private readonly IAuthenticator _authenticator;
        private readonly IGmailChangeProcessor _gmailChangeProcessor;
        private readonly ILogger _logger = Log.ForContext<GmailSynchronizer>();

        public GmailSynchronizer(MailAccount account,
                                 IAuthenticator authenticator,
                                 IGmailChangeProcessor gmailChangeProcessor) : base(account)
        {
            var messageHandler = new GmailClientMessageHandler(() => _authenticator.GetTokenAsync(Account));

            var initializer = new BaseClientService.Initializer()
            {
                HttpClientFactory = this
            };

            _gmailHttpClient = new ConfigurableHttpClient(messageHandler);
            _gmailService = new GmailService(initializer);
            _authenticator = authenticator;
            _gmailChangeProcessor = gmailChangeProcessor;
        }

        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => _gmailHttpClient;

        public override async Task<SynchronizationResult> SynchronizeInternalAsync(SynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            _logger.Information("Internal synchronization started for {Name}", Account.Name);

            // Gmail must always synchronize folders before because it doesn't have a per-folder sync.
            bool shouldSynchronizeFolders = true;

            if (shouldSynchronizeFolders)
            {
                _logger.Information("Synchronizing folders for {Name}", Account.Name);

                await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);

                _logger.Information("Synchronizing folders for {Name} is completed", Account.Name);
            }

            // There is no specific folder synchronization in Gmail.
            // Therefore we need to stop the synchronization at this point
            // if type is only folder metadata sync.

            if (options.Type == SynchronizationType.FoldersOnly) return SynchronizationResult.Empty;

            cancellationToken.ThrowIfCancellationRequested();

            bool isInitialSync = string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier);

            _logger.Debug("Is initial synchronization: {IsInitialSync}", isInitialSync);

            var missingMessageIds = new List<string>();

            var deltaChanges = new List<ListHistoryResponse>(); // For tracking delta changes.
            var listChanges = new List<ListMessagesResponse>(); // For tracking initial sync changes.

            /* Processing flow order is important to preserve the validity of history.
             * 1 - Process added mails. Because we need to create the mail first before assigning it to labels.
             * 2 - Process label assignments.
             * 3 - Process removed mails.
             * This affects reporting progres if done individually for each history change.
             * Therefore we need to process all changes in one go after the fetch.
             */

            if (isInitialSync)
            {
                // Initial synchronization.
                // Google sends message id and thread id in this query.
                // We'll collect them and send a Batch request to get details of the messages.

                var messageRequest = _gmailService.Users.Messages.List("me");

                // Gmail doesn't do per-folder sync. So our per-folder count is the same as total message count.
                messageRequest.MaxResults = InitialMessageDownloadCountPerFolder;
                messageRequest.IncludeSpamTrash = true;

                ListMessagesResponse result = null;

                string nextPageToken = string.Empty;

                while (true)
                {
                    if (!string.IsNullOrEmpty(nextPageToken))
                    {
                        messageRequest.PageToken = nextPageToken;
                    }

                    result = await messageRequest.ExecuteAsync(cancellationToken);

                    nextPageToken = result.NextPageToken;

                    listChanges.Add(result);

                    // Nothing to fetch anymore. Break the loop.
                    if (nextPageToken == null)
                        break;
                }
            }
            else
            {
                var startHistoryId = ulong.Parse(Account.SynchronizationDeltaIdentifier);
                var nextPageToken = ulong.Parse(Account.SynchronizationDeltaIdentifier).ToString();

                var historyRequest = _gmailService.Users.History.List("me");
                historyRequest.StartHistoryId = startHistoryId;

                while (!string.IsNullOrEmpty(nextPageToken))
                {
                    // If this is the first delta check, start from the last history id.
                    // Otherwise start from the next page token. We set them both to the same value for start.
                    // For each different page we set the page token to the next page token.

                    bool isFirstDeltaCheck = nextPageToken == startHistoryId.ToString();

                    if (!isFirstDeltaCheck)
                        historyRequest.PageToken = nextPageToken;

                    var historyResponse = await historyRequest.ExecuteAsync(cancellationToken);

                    nextPageToken = historyResponse.NextPageToken;

                    if (historyResponse.History == null)
                        continue;

                    deltaChanges.Add(historyResponse);
                }
            }

            // Add initial message ids from initial sync.
            missingMessageIds.AddRange(listChanges.Where(a => a.Messages != null).SelectMany(a => a.Messages).Select(a => a.Id));

            // Add missing message ids from delta changes.
            foreach (var historyResponse in deltaChanges)
            {
                var addedMessageIds = historyResponse.History
                    .Where(a => a.MessagesAdded != null)
                    .SelectMany(a => a.MessagesAdded)
                    .Where(a => a.Message != null)
                    .Select(a => a.Message.Id);

                missingMessageIds.AddRange(addedMessageIds);
            }

            // Consolidate added/deleted elements.
            // For example: History change might report downloading a mail first, then deleting it in another history change.
            // In that case, downloading mail will return entity not found error.
            // Plus, it's a redundant download the mail.
            // Purge missing message ids from potentially deleted mails to prevent this.

            var messageDeletedHistoryChanges = deltaChanges
                .Where(a => a.History != null)
                .SelectMany(a => a.History)
                .Where(a => a.MessagesDeleted != null)
                .SelectMany(a => a.MessagesDeleted);

            var deletedMailIdsInHistory = messageDeletedHistoryChanges.Select(a => a.Message.Id);

            if (deletedMailIdsInHistory.Any())
            {
                var mailIdsToConsolidate = missingMessageIds.Where(a => deletedMailIdsInHistory.Contains(a)).ToList();

                int consolidatedMessageCount = missingMessageIds.RemoveAll(a => deletedMailIdsInHistory.Contains(a));

                if (consolidatedMessageCount > 0)
                {
                    // TODO: Also delete the history changes that are related to these mails.
                    // This will prevent unwanted logs and additional queries to look for them in processing.

                    _logger.Information($"Purged {consolidatedMessageCount} missing mail downloads. ({string.Join(",", mailIdsToConsolidate)})");
                }
            }

            // Start downloading missing messages.
            await BatchDownloadMessagesAsync(missingMessageIds, cancellationToken).ConfigureAwait(false);

            // Map remote drafts to local drafts.
            await MapDraftIdsAsync(cancellationToken).ConfigureAwait(false);

            // Start processing delta changes.
            foreach (var historyResponse in deltaChanges)
            {
                await ProcessHistoryChangesAsync(historyResponse).ConfigureAwait(false);
            }

            // Take the max history id from delta changes and update the account sync modifier.
            var maxHistoryId = deltaChanges.Max(a => a.HistoryId);

            if (maxHistoryId != null)
            {
                // TODO: This is not good. Centralize the identifier fetch and prevent direct access here.
                Account.SynchronizationDeltaIdentifier = await _gmailChangeProcessor.UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, maxHistoryId.ToString()).ConfigureAwait(false);

                _logger.Debug("Final sync identifier {SynchronizationDeltaIdentifier}", Account.SynchronizationDeltaIdentifier);
            }

            // Get all unred new downloaded items and return in the result.
            // This is primarily used in notifications.

            var unreadNewItems = await _gmailChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, missingMessageIds).ConfigureAwait(false);

            return SynchronizationResult.Completed(unreadNewItems);
        }

        private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var localFolders = await _gmailChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
                var folderRequest = _gmailService.Users.Labels.List("me");

                var labelsResponse = await folderRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                if (labelsResponse.Labels == null)
                {
                    _logger.Warning("No folders found for {Name}", Account.Name);
                    return;
                }

                List<MailItemFolder> insertedFolders = new();
                List<MailItemFolder> updatedFolders = new();
                List<MailItemFolder> deletedFolders = new();

                // 1. Handle deleted labels.

                foreach (var localFolder in localFolders)
                {
                    // Category folder is virtual folder for Wino. Skip it.
                    if (localFolder.SpecialFolderType == SpecialFolderType.Category) continue;

                    var remoteFolder = labelsResponse.Labels.FirstOrDefault(a => a.Id == localFolder.RemoteFolderId);

                    if (remoteFolder == null)
                    {
                        // Local folder doesn't exists remotely. Delete local copy.
                        await _gmailChangeProcessor.DeleteFolderAsync(Account.Id, localFolder.RemoteFolderId).ConfigureAwait(false);

                        deletedFolders.Add(localFolder);
                    }
                }

                // Delete the deleted folders from local list.
                deletedFolders.ForEach(a => localFolders.Remove(a));

                // 2. Handle update/insert based on remote folders.
                foreach (var remoteFolder in labelsResponse.Labels)
                {
                    var existingLocalFolder = localFolders.FirstOrDefault(a => a.RemoteFolderId == remoteFolder.Id);

                    if (existingLocalFolder == null)
                    {
                        // Insert new folder.
                        var localFolder = remoteFolder.GetLocalFolder(labelsResponse, Account.Id);

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

                // 3.Process changes in order-> Insert, Update. Deleted ones are already processed.

                foreach (var folder in insertedFolders)
                {
                    await _gmailChangeProcessor.InsertFolderAsync(folder).ConfigureAwait(false);
                }

                foreach (var folder in updatedFolders)
                {
                    await _gmailChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        private bool ShouldUpdateFolder(Label remoteFolder, MailItemFolder existingLocalFolder)
            => existingLocalFolder.FolderName.Equals(GoogleIntegratorExtensions.GetFolderName(remoteFolder), StringComparison.OrdinalIgnoreCase) == false;

        /// <summary>
        /// Returns a single get request to retrieve the raw message with the given id
        /// </summary>
        /// <param name="messageId">Message to download.</param>
        /// <returns>Get request for raw mail.</returns>
        private UsersResource.MessagesResource.GetRequest CreateSingleMessageGet(string messageId)
        {
            var singleRequest = _gmailService.Users.Messages.Get("me", messageId);
            singleRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;

            return singleRequest;
        }

        /// <summary>
        /// Downloads given message ids per batch and processes them.
        /// </summary>
        /// <param name="messageIds">Gmail message ids to download.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        private async Task BatchDownloadMessagesAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
        {
            var totalDownloadCount = messageIds.Count();

            if (totalDownloadCount == 0) return;

            var downloadedItemCount = 0;

            _logger.Debug("Batch downloading {Count} messages for {Name}", messageIds.Count(), Account.Name);

            var allDownloadRequests = messageIds.Select(CreateSingleMessageGet);

            // Respect the batch size limit for batch requests.
            var batchedDownloadRequests = allDownloadRequests.Batch((int)MaximumAllowedBatchRequestSize);

            _logger.Debug("Total items to download: {TotalDownloadCount}. Created {Count} batch download requests for {Name}.", batchedDownloadRequests.Count(), Account.Name, totalDownloadCount);

            // Gmail SDK's BatchRequest has Action delegate for callback, not Task.
            // Therefore it's not possible to make sure that downloaded item is processed in the database before this
            // async callback is finished. Therefore we need to wrap all local database processings into task list and wait all of them to finish
            // Batch execution finishes after response parsing is done.

            var batchProcessCallbacks = new List<Task>();

            foreach (var batchBundle in batchedDownloadRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batchRequest = new BatchRequest(_gmailService);

                // Queue each request into this batch.
                batchBundle.ForEach(request =>
                {
                    batchRequest.Queue<Message>(request, (content, error, index, message) =>
                    {
                        var downloadingMessageId = messageIds.ElementAt(index);

                        batchProcessCallbacks.Add(HandleSingleItemDownloadedCallbackAsync(content, error, downloadingMessageId, cancellationToken));

                        downloadedItemCount++;

                        var progressValue = downloadedItemCount * 100 / Math.Max(1, totalDownloadCount);

                        PublishSynchronizationProgress(progressValue);
                    });
                });

                _logger.Information("Executing batch download with {Count} items.", batchRequest.Count);

                await batchRequest.ExecuteAsync(cancellationToken);

                // This is important due to bug in Gmail SDK.
                // We force GC here to prevent Out of Memory exception.
                // https://github.com/googleapis/google-api-dotnet-client/issues/2603

                GC.Collect();
            }

            // Wait for all processing to finish.
            await Task.WhenAll(batchProcessCallbacks).ConfigureAwait(false);
        }

        /// <summary>
        /// Processes the delta changes for the given history changes.
        /// Message downloads are not handled here since it's better to batch them.
        /// </summary>
        /// <param name="listHistoryResponse">List of history changes.</param>
        private async Task ProcessHistoryChangesAsync(ListHistoryResponse listHistoryResponse)
        {
            _logger.Debug("Processing delta change {HistoryId} for {Name}", Account.Name, listHistoryResponse.HistoryId.GetValueOrDefault());

            foreach (var history in listHistoryResponse.History)
            {
                // Handle label additions.
                if (history.LabelsAdded is not null)
                {
                    foreach (var addedLabel in history.LabelsAdded)
                    {
                        await HandleLabelAssignmentAsync(addedLabel);
                    }
                }

                // Handle label removals.
                if (history.LabelsRemoved is not null)
                {
                    foreach (var removedLabel in history.LabelsRemoved)
                    {
                        await HandleLabelRemovalAsync(removedLabel);
                    }
                }

                // Handle removed messages.
                if (history.MessagesDeleted is not null)
                {
                    foreach (var deletedMessage in history.MessagesDeleted)
                    {
                        var messageId = deletedMessage.Message.Id;

                        _logger.Debug("Processing message deletion for {MessageId}", messageId);

                        await _gmailChangeProcessor.DeleteMailAsync(Account.Id, messageId).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task HandleLabelAssignmentAsync(HistoryLabelAdded addedLabel)
        {
            var messageId = addedLabel.Message.Id;

            _logger.Debug("Processing label assignment for message {MessageId}", messageId);

            foreach (var labelId in addedLabel.LabelIds)
            {
                // When UNREAD label is added mark the message as un-read.
                if (labelId == GoogleIntegratorExtensions.UNREAD_LABEL_ID)
                    await _gmailChangeProcessor.ChangeMailReadStatusAsync(messageId, false).ConfigureAwait(false);

                // When STARRED label is added mark the message as flagged.
                if (labelId == GoogleIntegratorExtensions.STARRED_LABEL_ID)
                    await _gmailChangeProcessor.ChangeFlagStatusAsync(messageId, true).ConfigureAwait(false);

                await _gmailChangeProcessor.CreateAssignmentAsync(Account.Id, messageId, labelId).ConfigureAwait(false);
            }
        }

        private async Task HandleLabelRemovalAsync(HistoryLabelRemoved removedLabel)
        {
            var messageId = removedLabel.Message.Id;

            _logger.Debug("Processing label removed for message {MessageId}", messageId);

            foreach (var labelId in removedLabel.LabelIds)
            {
                // When UNREAD label is removed mark the message as read.
                if (labelId == GoogleIntegratorExtensions.UNREAD_LABEL_ID)
                    await _gmailChangeProcessor.ChangeMailReadStatusAsync(messageId, true).ConfigureAwait(false);

                // When STARRED label is removed mark the message as un-flagged.
                if (labelId == GoogleIntegratorExtensions.STARRED_LABEL_ID)
                    await _gmailChangeProcessor.ChangeFlagStatusAsync(messageId, false).ConfigureAwait(false);

                // For other labels remove the mail assignment.
                await _gmailChangeProcessor.DeleteAssignmentAsync(Account.Id, messageId, labelId).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Prepares Gmail Draft object from Google SDK.
        /// If provided, ThreadId ties the draft to a thread. Used when replying messages.
        /// If provided, DraftId updates the draft instead of creating a new one.
        /// </summary>
        /// <param name="mimeMessage">MailKit MimeMessage to include as raw message into Gmail request.</param>
        /// <param name="messageThreadId">ThreadId that this draft should be tied to.</param>
        /// <param name="messageDraftId">Existing DraftId from Gmail to update existing draft.</param>
        /// <returns></returns>
        private Draft PrepareGmailDraft(MimeMessage mimeMessage, string messageThreadId = "", string messageDraftId = "")
        {
            mimeMessage.Prepare(EncodingConstraint.None);

            var mimeString = mimeMessage.ToString();
            var base64UrlEncodedMime = Base64UrlEncoder.Encode(mimeString);

            var nativeMessage = new Message()
            {
                Raw = base64UrlEncodedMime,
            };

            if (!string.IsNullOrEmpty(messageThreadId))
                nativeMessage.ThreadId = messageThreadId;

            var draft = new Draft()
            {
                Message = nativeMessage,
                Id = messageDraftId
            };

            return draft;
        }

        #region Mail Integrations

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> Move(BatchMoveRequest request)
        {
            return CreateBatchedHttpBundleFromGroup(request, (items) =>
            {
                var batchModifyRequest = new BatchModifyMessagesRequest
                {
                    Ids = items.Select(a => a.Item.Id.ToString()).ToList(),
                    AddLabelIds = new[] { request.ToFolder.RemoteFolderId },
                    RemoveLabelIds = new[] { request.FromFolder.RemoteFolderId }
                };

                return _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");
            });
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> ChangeFlag(BatchChangeFlagRequest request)
        {
            return CreateBatchedHttpBundleFromGroup(request, (items) =>
            {
                var batchModifyRequest = new BatchModifyMessagesRequest
                {
                    Ids = items.Select(a => a.Item.Id.ToString()).ToList(),
                };

                if (request.IsFlagged)
                    batchModifyRequest.AddLabelIds = new List<string>() { GoogleIntegratorExtensions.STARRED_LABEL_ID };
                else
                    batchModifyRequest.RemoveLabelIds = new List<string>() { GoogleIntegratorExtensions.STARRED_LABEL_ID };

                return _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");
            });
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> MarkRead(BatchMarkReadRequest request)
        {
            return CreateBatchedHttpBundleFromGroup(request, (items) =>
            {
                var batchModifyRequest = new BatchModifyMessagesRequest
                {
                    Ids = items.Select(a => a.Item.Id.ToString()).ToList(),
                };

                if (request.IsRead)
                    batchModifyRequest.RemoveLabelIds = new List<string>() { GoogleIntegratorExtensions.UNREAD_LABEL_ID };
                else
                    batchModifyRequest.AddLabelIds = new List<string>() { GoogleIntegratorExtensions.UNREAD_LABEL_ID };

                return _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");
            });
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> Delete(BatchDeleteRequest request)
        {
            return CreateBatchedHttpBundleFromGroup(request, (items) =>
            {
                var batchModifyRequest = new BatchDeleteMessagesRequest
                {
                    Ids = items.Select(a => a.Item.Id.ToString()).ToList(),
                };

                return _gmailService.Users.Messages.BatchDelete(batchModifyRequest, "me");
            });
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> CreateDraft(BatchCreateDraftRequest request)
        {
            return CreateHttpBundle(request, (item) =>
            {
                if (item is not CreateDraftRequest singleRequest)
                    throw new ArgumentException("BatchCreateDraftRequest collection must be of type CreateDraftRequest.");

                Draft draft = null;

                // It's new mail. Not a reply
                if (singleRequest.DraftPreperationRequest.ReferenceMailCopy == null)
                    draft = PrepareGmailDraft(singleRequest.DraftPreperationRequest.CreatedLocalDraftMimeMessage);
                else
                    draft = PrepareGmailDraft(singleRequest.DraftPreperationRequest.CreatedLocalDraftMimeMessage,
                        singleRequest.DraftPreperationRequest.ReferenceMailCopy.ThreadId,
                        singleRequest.DraftPreperationRequest.ReferenceMailCopy.DraftId);

                return _gmailService.Users.Drafts.Create(draft, "me");
            });
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> Archive(BatchArchiveRequest request)
        {
            return CreateBatchedHttpBundleFromGroup(request, (items) =>
            {
                var batchModifyRequest = new BatchModifyMessagesRequest
                {
                    Ids = items.Select(a => a.Item.Id.ToString()).ToList()
                };

                if (request.IsArchiving)
                {
                    batchModifyRequest.RemoveLabelIds = new[] { GoogleIntegratorExtensions.INBOX_LABEL_ID };
                }
                else
                {
                    batchModifyRequest.AddLabelIds = new[] { GoogleIntegratorExtensions.INBOX_LABEL_ID };
                }

                return _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");
            });
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> SendDraft(BatchSendDraftRequestRequest request)
        {
            return CreateHttpBundle(request, (item) =>
            {
                if (item is not SendDraftRequest singleDraftRequest)
                    throw new ArgumentException("BatchSendDraftRequestRequest collection must be of type SendDraftRequest.");

                var message = new Message();

                if (!string.IsNullOrEmpty(singleDraftRequest.Item.ThreadId))
                {
                    message.ThreadId = singleDraftRequest.Item.ThreadId;
                }

                singleDraftRequest.Request.Mime.Prepare(EncodingConstraint.None);

                var mimeString = singleDraftRequest.Request.Mime.ToString();
                var base64UrlEncodedMime = Base64UrlEncoder.Encode(mimeString);
                message.Raw = base64UrlEncodedMime;

                var draft = new Draft()
                {
                    Id = singleDraftRequest.Request.MailItem.DraftId,
                    Message = message
                };

                return _gmailService.Users.Drafts.Send(draft, "me");
            });
        }

        public override async Task DownloadMissingMimeMessageAsync(IMailItem mailItem,
                                                               ITransferProgress transferProgress = null,
                                                               CancellationToken cancellationToken = default)
        {
            var request = _gmailService.Users.Messages.Get("me", mailItem.Id);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;

            var gmailMessage = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var mimeMessage = gmailMessage.GetGmailMimeMessage();

            if (mimeMessage == null)
            {
                _logger.Warning("Tried to download Gmail Raw Mime with {Id} id and server responded without a data.", mailItem.Id);
                return;
            }

            await _gmailChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> RenameFolder(RenameFolderRequest request)
        {
            return CreateHttpBundleWithResponse<Label>(request, (item) =>
            {
                if (item is not RenameFolderRequest renameFolderRequest)
                    throw new ArgumentException($"Renaming folder must be handled with '{nameof(RenameFolderRequest)}'");

                var label = new Label()
                {
                    Name = renameFolderRequest.NewFolderName
                };

                return _gmailService.Users.Labels.Update(label, "me", request.Folder.RemoteFolderId);
            });
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> EmptyFolder(EmptyFolderRequest request)
        {
            // Create batch delete request.

            var deleteRequests = request.MailsToDelete.Select(a => new DeleteRequest(a));

            return Delete(new BatchDeleteRequest(deleteRequests));
        }

        public override IEnumerable<IRequestBundle<IClientServiceRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
            => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true)), true));

        #endregion

        #region Request Execution

        public override async Task ExecuteNativeRequestsAsync(IEnumerable<IRequestBundle<IClientServiceRequest>> batchedRequests,
                                                              CancellationToken cancellationToken = default)
        {
            var batchedBundles = batchedRequests.Batch((int)MaximumAllowedBatchRequestSize);
            var bundleCount = batchedBundles.Count();

            for (int i = 0; i < bundleCount; i++)
            {
                var bundle = batchedBundles.ElementAt(i);

                var nativeBatchRequest = new BatchRequest(_gmailService);

                var bundleRequestCount = bundle.Count();

                for (int k = 0; k < bundleRequestCount; k++)
                {
                    var requestBundle = bundle.ElementAt(k);

                    var nativeRequest = requestBundle.NativeRequest;
                    var request = requestBundle.Request;

                    request.ApplyUIChanges();

                    // TODO: Queue is synchronous. Create a task bucket to await all processing.
                    nativeBatchRequest.Queue<object>(nativeRequest, async (content, error, index, message)
                        => await ProcessSingleNativeRequestResponseAsync(requestBundle, error, message, cancellationToken).ConfigureAwait(false));
                }

                await nativeBatchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void ProcessGmailRequestError(RequestError error)
        {
            if (error == null) return;

            // OutOfMemoryException is a known bug in Gmail SDK.
            if (error.Code == 0)
            {
                throw new OutOfMemoryException(error.Message);
            }

            // Entity not found.
            if (error.Code == 404)
            {
                throw new SynchronizerEntityNotFoundException(error.Message);
            }

            if (!string.IsNullOrEmpty(error.Message))
            {
                error.Errors?.ForEach(error => _logger.Error("Unknown Gmail SDK error for {Name}\n{Error}", Account.Name, error));

                // TODO: Debug
                // throw new SynchronizerException(error.Message);
            }
        }

        /// <summary>
        /// Handles after each single message download.
        /// This involves adding the Gmail message into Wino database.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="error"></param>
        /// <param name="httpResponseMessage"></param>
        /// <param name="cancellationToken"></param>
        private async Task HandleSingleItemDownloadedCallbackAsync(Message message,
                                                                   RequestError error,
                                                                   string downloadingMessageId,
                                                                   CancellationToken cancellationToken = default)
        {
            try
            {
                ProcessGmailRequestError(error);
            }
            catch (OutOfMemoryException)
            {
                _logger.Warning("Gmail SDK got OutOfMemoryException due to bug in the SDK");
            }
            catch (SynchronizerEntityNotFoundException)
            {
                _logger.Warning("Resource not found for {DownloadingMessageId}", downloadingMessageId);
            }
            catch (SynchronizerException synchronizerException)
            {
                _logger.Error("Gmail SDK returned error for {DownloadingMessageId}\n{SynchronizerException}", downloadingMessageId, synchronizerException);
            }

            if (message == null)
            {
                _logger.Warning("Skipped GMail message download for {DownloadingMessageId}", downloadingMessageId);

                return;
            }

            // Gmail has LabelId property for each message.
            // Therefore we can pass null as the assigned folder safely.
            var mailPackage = await CreateNewMailPackagesAsync(message, null, cancellationToken);

            // If CreateNewMailPackagesAsync returns null it means local draft mapping is done.
            // We don't need to insert anything else.
            if (mailPackage == null)
                return;

            foreach (var package in mailPackage)
            {
                await _gmailChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);
            }

            // Try updating the history change identifier if any.
            if (message.HistoryId == null) return;

            // Delta changes also has history id but the maximum id is preserved in the account service.
            // TODO: This is not good. Centralize the identifier fetch and prevent direct access here.
            Account.SynchronizationDeltaIdentifier = await _gmailChangeProcessor.UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, message.HistoryId.ToString());
        }

        private async Task ProcessSingleNativeRequestResponseAsync(IRequestBundle<IClientServiceRequest> bundle,
                                                                   RequestError error,
                                                                   HttpResponseMessage httpResponseMessage,
                                                                   CancellationToken cancellationToken = default)
        {
            ProcessGmailRequestError(error);

            if (bundle is HttpRequestBundle<IClientServiceRequest, Message> messageBundle)
            {
                var gmailMessage = await messageBundle.DeserializeBundleAsync(httpResponseMessage, cancellationToken).ConfigureAwait(false);

                if (gmailMessage == null) return;

                await HandleSingleItemDownloadedCallbackAsync(gmailMessage, error, "unknown", cancellationToken);
            }
            else if (bundle is HttpRequestBundle<IClientServiceRequest, Label> folderBundle)
            {
                var gmailLabel = await folderBundle.DeserializeBundleAsync(httpResponseMessage, cancellationToken).ConfigureAwait(false);

                if (gmailLabel == null) return;

                // TODO: Handle new Gmail Label added or updated.
            }
            else if (bundle is HttpRequestBundle<IClientServiceRequest, Draft> draftBundle && draftBundle.Request is CreateDraftRequest createDraftRequest)
            {
                // New draft mail is created.

                var messageDraft = await draftBundle.DeserializeBundleAsync(httpResponseMessage, cancellationToken).ConfigureAwait(false);

                if (messageDraft == null) return;

                var localDraftCopy = createDraftRequest.DraftPreperationRequest.CreatedLocalDraftCopy;

                // Here we have DraftId, MessageId and ThreadId.
                // Update the local copy properties and re-synchronize to get the original message and update history.

                // We don't fetch the single message here because it may skip some of the history changes when the
                // fetch updates the historyId. Therefore we need to re-synchronize to get the latest history changes
                // which will have the original message downloaded eventually.

                await _gmailChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopy.UniqueId, messageDraft.Message.Id, messageDraft.Id, messageDraft.Message.ThreadId);

                var options = new SynchronizationOptions()
                {
                    AccountId = Account.Id,
                    Type = SynchronizationType.Full
                };

                await SynchronizeInternalAsync(options, cancellationToken);
            }
        }


        /// <summary>
        /// Maps existing Gmail Draft resources to local mail copies.
        /// This uses indexed search, therefore it's quite fast.
        /// It's safe to execute this after each Draft creation + batch message download.
        /// </summary>
        private async Task MapDraftIdsAsync(CancellationToken cancellationToken = default)
        {
            // TODO: This call is not necessary if we don't have any local drafts.
            // Remote drafts will be downloaded in missing message batches anyways.
            // Fix it by checking whether we need to do this or not.

            var drafts = await _gmailService.Users.Drafts.List("me").ExecuteAsync(cancellationToken);

            if (drafts.Drafts == null)
            {
                _logger.Information("There are no drafts to map for {Name}", Account.Name);

                return;
            }

            foreach (var draft in drafts.Drafts)
            {
                await _gmailChangeProcessor.MapLocalDraftAsync(draft.Message.Id, draft.Id, draft.Message.ThreadId);
            }
        }

        /// <summary>
        /// Creates new mail packages for the given message.
        /// AssignedFolder is null since the LabelId is parsed out of the Message.
        /// </summary>
        /// <param name="message">Gmail message to create package for.</param>
        /// <param name="assignedFolder">Null, not used.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>New mail package that change processor can use to insert new mail into database.</returns>
        public override async Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(Message message,
                                                                                  MailItemFolder assignedFolder,
                                                                                  CancellationToken cancellationToken = default)
        {
            var packageList = new List<NewMailItemPackage>();

            MimeMessage mimeMessage = message.GetGmailMimeMessage();
            var mailCopy = message.AsMailCopy(mimeMessage);

            // Check whether this message is mapped to any local draft.
            // Previously we were using Draft resource response as mapping drafts.
            // This seem to be a worse approach. Now both Outlook and Gmail use X-Wino-Draft-Id header to map drafts.
            // This is a better approach since we don't need to fetch the draft resource to get the draft id.

            if (mailCopy.IsDraft
                && mimeMessage.Headers.Contains(Domain.Constants.WinoLocalDraftHeader)
                && Guid.TryParse(mimeMessage.Headers[Domain.Constants.WinoLocalDraftHeader], out Guid localDraftCopyUniqueId))
            {
                // This message belongs to existing local draft copy.
                // We don't need to create a new mail copy for this message, just update the existing one.

                bool isMappingSuccesfull = await _gmailChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopyUniqueId, mailCopy.Id, mailCopy.DraftId, mailCopy.ThreadId);

                if (isMappingSuccesfull) return null;

                // Local copy doesn't exists. Continue execution to insert mail copy.
            }

            if (message.LabelIds is not null)
            {
                foreach (var labelId in message.LabelIds)
                {
                    packageList.Add(new NewMailItemPackage(mailCopy, mimeMessage, labelId));
                }
            }

            return packageList;
        }

        #endregion
    }
}

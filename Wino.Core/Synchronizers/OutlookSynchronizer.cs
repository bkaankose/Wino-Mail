using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using MimeKit;
using MoreLinq.Extensions;
using Serilog;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Http;
using Wino.Core.Integration.Processors;
using Wino.Core.Misc;
using Wino.Core.Requests;

namespace Wino.Core.Synchronizers
{
    public class OutlookSynchronizer : BaseSynchronizer<RequestInformation, Message>
    {
        public override uint BatchModificationSize => 20;
        public override uint InitialMessageDownloadCountPerFolder => 250;
        private const uint MaximumAllowedBatchRequestSize = 20;

        private const string INBOX_NAME = "inbox";
        private const string SENT_NAME = "sentitems";
        private const string DELETED_NAME = "deleteditems";
        private const string JUNK_NAME = "junkemail";
        private const string DRAFTS_NAME = "drafts";
        private const string ARCHIVE_NAME = "archive";

        private readonly string[] outlookMessageSelectParameters =
        [
            "InferenceClassification",
            "Flag",
            "Importance",
            "IsRead",
            "IsDraft",
            "ReceivedDateTime",
            "HasAttachments",
            "BodyPreview",
            "Id",
            "ConversationId",
            "From",
            "Subject",
            "ParentFolderId",
            "InternetMessageId",
        ];

        private readonly SemaphoreSlim _handleItemRetrievalSemaphore = new(1);

        private readonly ILogger _logger = Log.ForContext<OutlookSynchronizer>();
        private readonly IOutlookChangeProcessor _outlookChangeProcessor;
        private readonly GraphServiceClient _graphClient;
        public OutlookSynchronizer(MailAccount account,
                                   IAuthenticator authenticator,
                                   IOutlookChangeProcessor outlookChangeProcessor) : base(account)
        {
            var tokenProvider = new MicrosoftTokenProvider(Account, authenticator);

            // Update request handlers for Graph client.
            var handlers = GraphClientFactory.CreateDefaultHandlers();

            handlers.Add(GetMicrosoftImmutableIdHandler());

            // Remove existing RetryHandler and add a new one with custom options.
            var existingRetryHandler = handlers.FirstOrDefault(a => a is RetryHandler);
            if (existingRetryHandler != null)
                handlers.Remove(existingRetryHandler);

            // Add custom one.
            handlers.Add(GetRetryHandler());

            var httpClient = GraphClientFactory.Create(handlers);
            _graphClient = new GraphServiceClient(httpClient, new BaseBearerTokenAuthenticationProvider(tokenProvider));

            _outlookChangeProcessor = outlookChangeProcessor;

            // Specify to use TLS 1.2 as default connection
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        #region MS Graph Handlers

        private MicrosoftImmutableIdHandler GetMicrosoftImmutableIdHandler() => new();

        private RetryHandler GetRetryHandler()
        {
            var options = new RetryHandlerOption()
            {
                ShouldRetry = (delay, attempt, httpResponse) =>
                {
                    var statusCode = httpResponse.StatusCode;

                    return statusCode switch
                    {
                        HttpStatusCode.ServiceUnavailable => true,
                        HttpStatusCode.GatewayTimeout => true,
                        (HttpStatusCode)429 => true,
                        HttpStatusCode.Unauthorized => true,
                        _ => false
                    };
                },
                Delay = 3,
                MaxRetry = 3
            };

            return new RetryHandler(options);
        }

        #endregion


        protected override async Task<SynchronizationResult> SynchronizeInternalAsync(SynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            var downloadedMessageIds = new List<string>();

            _logger.Information("Internal synchronization started for {Name}", Account.Name);
            _logger.Information("Options: {Options}", options);

            try
            {
                PublishSynchronizationProgress(1);

                await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);

                if (options.Type != SynchronizationType.FoldersOnly)
                {
                    var synchronizationFolders = await _outlookChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);

                    _logger.Information(string.Format("{1} Folders: {0}", string.Join(",", synchronizationFolders.Select(a => a.FolderName)), synchronizationFolders.Count));

                    for (int i = 0; i < synchronizationFolders.Count; i++)
                    {
                        var folder = synchronizationFolders[i];
                        var progress = (int)Math.Round((double)(i + 1) / synchronizationFolders.Count * 100);

                        PublishSynchronizationProgress(progress);

                        var folderDownloadedMessageIds = await SynchronizeFolderAsync(folder, cancellationToken).ConfigureAwait(false);
                        downloadedMessageIds.AddRange(folderDownloadedMessageIds);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Synchronizing folders for {Name}", Account.Name);
                Debugger.Break();

                throw;
            }
            finally
            {
                PublishSynchronizationProgress(100);
            }

            // Get all unred new downloaded items and return in the result.
            // This is primarily used in notifications.

            var unreadNewItems = await _outlookChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);

            return SynchronizationResult.Completed(unreadNewItems);
        }

        private async Task<IEnumerable<string>> SynchronizeFolderAsync(MailItemFolder folder, CancellationToken cancellationToken = default)
        {
            var downloadedMessageIds = new List<string>();

            cancellationToken.ThrowIfCancellationRequested();

            string latestDeltaLink = string.Empty;

            bool isInitialSync = string.IsNullOrEmpty(folder.DeltaToken);

            Microsoft.Graph.Me.MailFolders.Item.Messages.Delta.DeltaGetResponse messageCollectionPage = null;

            _logger.Debug("Synchronizing {FolderName}", folder.FolderName);

            if (isInitialSync)
            {
                _logger.Debug("No sync identifier for Folder {FolderName}. Performing initial sync.", folder.FolderName);

                // No delta link. Performing initial sync.

                messageCollectionPage = await _graphClient.Me.MailFolders[folder.RemoteFolderId].Messages.Delta.GetAsDeltaGetResponseAsync((config) =>
               {
                   config.QueryParameters.Top = (int)InitialMessageDownloadCountPerFolder;
                   config.QueryParameters.Select = outlookMessageSelectParameters;
                   config.QueryParameters.Orderby = ["receivedDateTime desc"];
               }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var currentDeltaToken = folder.DeltaToken;

                var requestInformation = _graphClient.Me.MailFolders[folder.RemoteFolderId].Messages.Delta.ToGetRequestInformation((config) =>
                {
                    config.QueryParameters.Top = (int)InitialMessageDownloadCountPerFolder;
                    config.QueryParameters.Select = outlookMessageSelectParameters;
                    config.QueryParameters.Orderby = ["receivedDateTime desc"];
                });

                requestInformation.UrlTemplate = requestInformation.UrlTemplate.Insert(requestInformation.UrlTemplate.Length - 1, ",%24deltatoken");
                requestInformation.QueryParameters.Add("%24deltatoken", currentDeltaToken);

                messageCollectionPage = await _graphClient.RequestAdapter.SendAsync(requestInformation, Microsoft.Graph.Me.MailFolders.Item.Messages.Delta.DeltaGetResponse.CreateFromDiscriminatorValue);
            }

            var messageIteratorAsync = PageIterator<Message, Microsoft.Graph.Me.MailFolders.Item.Messages.Delta.DeltaGetResponse>.CreatePageIterator(_graphClient, messageCollectionPage, async (item) =>
            {
                try
                {
                    await _handleItemRetrievalSemaphore.WaitAsync();
                    return await HandleItemRetrievedAsync(item, folder, downloadedMessageIds, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error occurred while handling item {Id} for folder {FolderName}", item.Id, folder.FolderName);
                }
                finally
                {
                    _handleItemRetrievalSemaphore.Release();
                }

                return true;
            });

            await messageIteratorAsync
                .IterateAsync(cancellationToken)
                .ConfigureAwait(false);

            latestDeltaLink = messageIteratorAsync.Deltalink;

            if (downloadedMessageIds.Any())
            {
                _logger.Debug("Downloaded {Count} messages for folder {FolderName}", downloadedMessageIds.Count, folder.FolderName);
            }

            //Store delta link for tracking new changes.
            if (!string.IsNullOrEmpty(latestDeltaLink))
            {
                // Parse Delta Token from Delta Link since v5 of Graph SDK works based on the token, not the link.

                var deltaToken = GetDeltaTokenFromDeltaLink(latestDeltaLink);

                await _outlookChangeProcessor.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, deltaToken).ConfigureAwait(false);
            }

            await _outlookChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

            return downloadedMessageIds;
        }

        private string GetDeltaTokenFromDeltaLink(string deltaLink)
            => Regex.Split(deltaLink, "deltatoken=")[1];

        private bool IsResourceDeleted(IDictionary<string, object> additionalData)
            => additionalData != null && additionalData.ContainsKey("@removed");

        private async Task<bool> HandleFolderRetrievedAsync(MailFolder folder, OutlookSpecialFolderIdInformation outlookSpecialFolderIdInformation, CancellationToken cancellationToken = default)
        {
            if (IsResourceDeleted(folder.AdditionalData))
            {
                await _outlookChangeProcessor.DeleteFolderAsync(Account.Id, folder.Id).ConfigureAwait(false);
            }
            else
            {
                // New folder created.

                var item = folder.GetLocalFolder(Account.Id);

                if (item.RemoteFolderId.Equals(outlookSpecialFolderIdInformation.InboxId))
                    item.SpecialFolderType = SpecialFolderType.Inbox;
                else if (item.RemoteFolderId.Equals(outlookSpecialFolderIdInformation.SentId))
                    item.SpecialFolderType = SpecialFolderType.Sent;
                else if (item.RemoteFolderId.Equals(outlookSpecialFolderIdInformation.DraftId))
                    item.SpecialFolderType = SpecialFolderType.Draft;
                else if (item.RemoteFolderId.Equals(outlookSpecialFolderIdInformation.TrashId))
                    item.SpecialFolderType = SpecialFolderType.Deleted;
                else if (item.RemoteFolderId.Equals(outlookSpecialFolderIdInformation.JunkId))
                    item.SpecialFolderType = SpecialFolderType.Junk;
                else if (item.RemoteFolderId.Equals(outlookSpecialFolderIdInformation.ArchiveId))
                    item.SpecialFolderType = SpecialFolderType.Archive;
                else
                    item.SpecialFolderType = SpecialFolderType.Other;

                // Automatically mark special folders as Sticky for better visibility.
                item.IsSticky = item.SpecialFolderType != SpecialFolderType.Other;

                // By default, all non-others are system folder.
                item.IsSystemFolder = item.SpecialFolderType != SpecialFolderType.Other;

                // By default, all special folders update unread count in the UI except Trash.
                item.ShowUnreadCount = item.SpecialFolderType != SpecialFolderType.Deleted || item.SpecialFolderType != SpecialFolderType.Other;

                await _outlookChangeProcessor.InsertFolderAsync(item).ConfigureAwait(false);
            }

            return true;
        }

        private async Task<bool> HandleItemRetrievedAsync(Message item, MailItemFolder folder, IList<string> downloadedMessageIds, CancellationToken cancellationToken = default)
        {
            if (IsResourceDeleted(item.AdditionalData))
            {
                // Deleting item with this override instead of the other one that deletes all mail copies.
                // Outlook mails have 1 assignment per-folder, unlike Gmail that has one to many.

                await _outlookChangeProcessor.DeleteAssignmentAsync(Account.Id, item.Id, folder.RemoteFolderId).ConfigureAwait(false);
            }
            else
            {
                // If the item exists in the local database, it means that it's already downloaded. Process as an Update.

                var isMailExists = await _outlookChangeProcessor.IsMailExistsInFolderAsync(item.Id, folder.Id);

                if (isMailExists)
                {
                    // Some of the properties of the item are updated.

                    if (item.IsRead != null)
                    {
                        await _outlookChangeProcessor.ChangeMailReadStatusAsync(item.Id, item.IsRead.GetValueOrDefault()).ConfigureAwait(false);
                    }

                    if (item.Flag?.FlagStatus != null)
                    {
                        await _outlookChangeProcessor.ChangeFlagStatusAsync(item.Id, item.Flag.FlagStatus.GetValueOrDefault() == FollowupFlagStatus.Flagged)
                                                     .ConfigureAwait(false);
                    }
                }
                else
                {
                    // Package may return null on some cases mapping the remote draft to existing local draft.

                    var newMailPackages = await CreateNewMailPackagesAsync(item, folder, cancellationToken);

                    if (newMailPackages != null)
                    {
                        foreach (var package in newMailPackages)
                        {
                            // Only add to downloaded message ids if it's inserted successfuly.
                            // Updates should not be added to the list because they are not new.
                            bool isInserted = await _outlookChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);

                            if (isInserted)
                            {
                                downloadedMessageIds.Add(package.Copy.Id);
                            }
                        }
                    }
                }
            }

            return true;
        }

        private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken = default)
        {
        // Gather special folders by default.
        // Others will be other type.

        // Get well known folder ids by batch.

        retry:
            var wellKnownFolderIdBatch = new BatchRequestContentCollection(_graphClient);

            var inboxRequest = _graphClient.Me.MailFolders[INBOX_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; });
            var sentRequest = _graphClient.Me.MailFolders[SENT_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; });
            var deletedRequest = _graphClient.Me.MailFolders[DELETED_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; });
            var junkRequest = _graphClient.Me.MailFolders[JUNK_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; });
            var draftsRequest = _graphClient.Me.MailFolders[DRAFTS_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; });
            var archiveRequest = _graphClient.Me.MailFolders[ARCHIVE_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; });

            var inboxId = await wellKnownFolderIdBatch.AddBatchRequestStepAsync(inboxRequest);
            var sentId = await wellKnownFolderIdBatch.AddBatchRequestStepAsync(sentRequest);
            var deletedId = await wellKnownFolderIdBatch.AddBatchRequestStepAsync(deletedRequest);
            var junkId = await wellKnownFolderIdBatch.AddBatchRequestStepAsync(junkRequest);
            var draftsId = await wellKnownFolderIdBatch.AddBatchRequestStepAsync(draftsRequest);
            var archiveId = await wellKnownFolderIdBatch.AddBatchRequestStepAsync(archiveRequest);

            var returnedResponse = await _graphClient.Batch.PostAsync(wellKnownFolderIdBatch, cancellationToken).ConfigureAwait(false);

            var inboxFolderId = (await returnedResponse.GetResponseByIdAsync<MailFolder>(inboxId)).Id;
            var sentFolderId = (await returnedResponse.GetResponseByIdAsync<MailFolder>(sentId)).Id;
            var deletedFolderId = (await returnedResponse.GetResponseByIdAsync<MailFolder>(deletedId)).Id;
            var junkFolderId = (await returnedResponse.GetResponseByIdAsync<MailFolder>(junkId)).Id;
            var draftsFolderId = (await returnedResponse.GetResponseByIdAsync<MailFolder>(draftsId)).Id;
            var archiveFolderId = (await returnedResponse.GetResponseByIdAsync<MailFolder>(archiveId)).Id;

            var specialFolderInfo = new OutlookSpecialFolderIdInformation(inboxFolderId, deletedFolderId, junkFolderId, draftsFolderId, sentFolderId, archiveFolderId);

            Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse graphFolders = null;

            if (string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier))
            {
                // Initial folder sync.

                var deltaRequest = _graphClient.Me.MailFolders.Delta.ToGetRequestInformation();

                deltaRequest.UrlTemplate = deltaRequest.UrlTemplate.Insert(deltaRequest.UrlTemplate.Length - 1, ",includehiddenfolders");
                deltaRequest.QueryParameters.Add("includehiddenfolders", "true");

                graphFolders = await _graphClient.RequestAdapter.SendAsync(deltaRequest,
                    Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse.CreateFromDiscriminatorValue,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var currentDeltaLink = Account.SynchronizationDeltaIdentifier;

                var deltaRequest = _graphClient.Me.MailFolders.Delta.ToGetRequestInformation();

                deltaRequest.UrlTemplate = deltaRequest.UrlTemplate.Insert(deltaRequest.UrlTemplate.Length - 1, ",%24deltaToken");
                deltaRequest.QueryParameters.Add("%24deltaToken", currentDeltaLink);

                try
                {
                    graphFolders = await _graphClient.RequestAdapter.SendAsync(deltaRequest,
                    Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse.CreateFromDiscriminatorValue,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (ApiException apiException) when (apiException.ResponseStatusCode == 410)
                {
                    Account.SynchronizationDeltaIdentifier = await _outlookChangeProcessor.ResetAccountDeltaTokenAsync(Account.Id);

                    goto retry;
                }
            }

            var iterator = PageIterator<MailFolder, Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse>.CreatePageIterator(_graphClient, graphFolders, (folder) =>
            {
                return HandleFolderRetrievedAsync(folder, specialFolderInfo, cancellationToken);
            });

            await iterator.IterateAsync();

            if (!string.IsNullOrEmpty(iterator.Deltalink))
            {
                // Get the second part of the query that its the deltaToken
                var deltaToken = iterator.Deltalink.Split('=')[1];

                var latestAccountDeltaToken = await _outlookChangeProcessor.UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, deltaToken);

                if (!string.IsNullOrEmpty(latestAccountDeltaToken))
                {
                    Account.SynchronizationDeltaIdentifier = latestAccountDeltaToken;
                }
            }
        }

        /// <summary>
        /// Get the user's profile picture
        /// </summary>
        /// <returns>Base64 encoded profile picture.</returns>
        private async Task<string> GetUserProfilePictureAsync()
        {
            try
            {
                var photoStream = await _graphClient.Me.Photos["48x48"].Content.GetAsync();

                using var memoryStream = new MemoryStream();
                await photoStream.CopyToAsync(memoryStream);
                var byteArray = memoryStream.ToArray();

                return Convert.ToBase64String(byteArray);
            }
            catch (ODataError odataError) when (odataError.Error.Code == "ImageNotFound")
            {
                // Accounts without profile picture will throw this error.
                // At this point nothing we can do. Just return empty string.

                return string.Empty;
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Get the user's display name.
        /// </summary>
        /// <returns>Display name of the user.</returns>
        private async Task<string> GetSenderNameAsync()
        {
            var userInfo = await _graphClient.Users["me"].GetAsync();

            return userInfo.DisplayName;
        }

        public override async Task<ProfileInformation> GetProfileInformationAsync()
        {
            var profilePictureData = await GetUserProfilePictureAsync().ConfigureAwait(false);
            var senderName = await GetSenderNameAsync().ConfigureAwait(false);

            return new ProfileInformation(senderName, profilePictureData);
        }

        /// <summary>
        /// POST requests are handled differently in batches in Graph SDK.
        /// Batch basically ignores the step's coontent-type and body.
        /// Manually create a POST request with empty body and send it.
        /// </summary>
        /// <param name="requestInformation">Post request information.</param>
        /// <param name="content">Content object to serialize.</param>
        /// <returns>Updated post request information.</returns>
        private RequestInformation PreparePostRequestInformation(RequestInformation requestInformation, object content = null)
        {
            requestInformation.Headers.Clear();

            string contentJson = content == null ? "{}" : JsonSerializer.Serialize(content);

            requestInformation.Content = new MemoryStream(Encoding.UTF8.GetBytes(contentJson));
            requestInformation.HttpMethod = Method.POST;
            requestInformation.Headers.Add("Content-Type", "application/json");

            return requestInformation;
        }

        #region Mail Integration

        public override bool DelaySendOperationSynchronization() => true;

        public override IEnumerable<IRequestBundle<RequestInformation>> Move(BatchMoveRequest request)
        {
            var requestBody = new Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody()
            {
                DestinationId = request.ToFolder.RemoteFolderId
            };

            return CreateBatchedHttpBundle(request, (item) =>
            {
                return PreparePostRequestInformation(_graphClient.Me.Messages[item.Item.Id.ToString()].Move.ToPostRequestInformation(requestBody),
                                                     requestBody);
            });
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> ChangeFlag(BatchChangeFlagRequest request)
        {
            return CreateBatchedHttpBundle(request, (item) =>
            {
                var message = new Message()
                {
                    Flag = new FollowupFlag() { FlagStatus = request.IsFlagged ? FollowupFlagStatus.Flagged : FollowupFlagStatus.NotFlagged }
                };

                return _graphClient.Me.Messages[item.Item.Id.ToString()].ToPatchRequestInformation(message);
            });
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> MarkRead(BatchMarkReadRequest request)
        {
            return CreateBatchedHttpBundle(request, (item) =>
            {
                var message = new Message()
                {
                    IsRead = request.IsRead
                };

                return _graphClient.Me.Messages[item.Item.Id].ToPatchRequestInformation(message);
            });
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> Delete(BatchDeleteRequest request)
        {
            return CreateBatchedHttpBundle(request, (item) =>
            {
                return _graphClient.Me.Messages[item.Item.Id].ToDeleteRequestInformation();
            });
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> MoveToFocused(BatchMoveToFocusedRequest request)
        {
            return CreateBatchedHttpBundleFromGroup(request, (item) =>
            {
                if (item is MoveToFocusedRequest moveToFocusedRequest)
                {
                    var message = new Message()
                    {
                        InferenceClassification = moveToFocusedRequest.MoveToFocused ? InferenceClassificationType.Focused : InferenceClassificationType.Other
                    };

                    return _graphClient.Me.Messages[moveToFocusedRequest.Item.Id].ToPatchRequestInformation(message);
                }

                throw new Exception("Invalid request type.");
            });

        }

        public override IEnumerable<IRequestBundle<RequestInformation>> AlwaysMoveTo(BatchAlwaysMoveToRequest request)
        {
            return CreateBatchedHttpBundle<Message>(request, (item) =>
            {
                if (item is AlwaysMoveToRequest alwaysMoveToRequest)
                {
                    var inferenceClassificationOverride = new InferenceClassificationOverride
                    {
                        ClassifyAs = alwaysMoveToRequest.MoveToFocused ? InferenceClassificationType.Focused : InferenceClassificationType.Other,
                        SenderEmailAddress = new EmailAddress
                        {
                            Name = alwaysMoveToRequest.Item.FromName,
                            Address = alwaysMoveToRequest.Item.FromAddress
                        }
                    };

                    return _graphClient.Me.InferenceClassification.Overrides.ToPostRequestInformation(inferenceClassificationOverride);
                }

                throw new Exception("Invalid request type.");
            });
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> CreateDraft(BatchCreateDraftRequest request)
        {
            return CreateHttpBundle<Message>(request, (item) =>
            {
                if (item is CreateDraftRequest createDraftRequest)
                {
                    var reason = createDraftRequest.DraftPreperationRequest.Reason;
                    var message = createDraftRequest.DraftPreperationRequest.CreatedLocalDraftMimeMessage.AsOutlookMessage(true);

                    if (reason == DraftCreationReason.Empty)
                    {
                        return _graphClient.Me.Messages.ToPostRequestInformation(message);
                    }
                    else if (reason == DraftCreationReason.Reply)
                    {
                        return _graphClient.Me.Messages[createDraftRequest.DraftPreperationRequest.ReferenceMailCopy.Id].CreateReply.ToPostRequestInformation(new Microsoft.Graph.Me.Messages.Item.CreateReply.CreateReplyPostRequestBody()
                        {
                            Message = message
                        });
                    }
                    else if (reason == DraftCreationReason.ReplyAll)
                    {
                        return _graphClient.Me.Messages[createDraftRequest.DraftPreperationRequest.ReferenceMailCopy.Id].CreateReplyAll.ToPostRequestInformation(new Microsoft.Graph.Me.Messages.Item.CreateReplyAll.CreateReplyAllPostRequestBody()
                        {
                            Message = message
                        });
                    }
                    else if (reason == DraftCreationReason.Forward)
                    {
                        return _graphClient.Me.Messages[createDraftRequest.DraftPreperationRequest.ReferenceMailCopy.Id].CreateForward.ToPostRequestInformation(new Microsoft.Graph.Me.Messages.Item.CreateForward.CreateForwardPostRequestBody()
                        {
                            Message = message
                        });
                    }
                }

                throw new Exception("Invalid create draft request type.");
            });
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> SendDraft(BatchSendDraftRequestRequest request)
        {
            var sendDraftPreparationRequest = request.Request;

            // 1. Delete draft
            // 2. Create new Message with new MIME.
            // 3. Make sure that conversation id is tagged correctly for replies.

            var mailCopyId = sendDraftPreparationRequest.MailItem.Id;
            var mimeMessage = sendDraftPreparationRequest.Mime;

            // Convert mime message to Outlook message.
            // Outlook synchronizer does not send MIME messages directly anymore.
            // Alias support is lacking with direct MIMEs.
            // Therefore we convert the MIME message to Outlook message and use proper APIs.

            var outlookMessage = mimeMessage.AsOutlookMessage(false);

            // Create attachment requests.
            // TODO: We need to support large file attachments with sessioned upload at some point.

            var attachmentRequestList = CreateAttachmentUploadBundles(mimeMessage, mailCopyId, request).ToList();

            // Update draft.

            var patchDraftRequest = _graphClient.Me.Messages[mailCopyId].ToPatchRequestInformation(outlookMessage);
            var patchDraftRequestBundle = new HttpRequestBundle<RequestInformation>(patchDraftRequest, request);

            // Send draft.

            var sendDraftRequest = PreparePostRequestInformation(_graphClient.Me.Messages[mailCopyId].Send.ToPostRequestInformation());
            var sendDraftRequestBundle = new HttpRequestBundle<RequestInformation>(sendDraftRequest, request);

            return [.. attachmentRequestList, patchDraftRequestBundle, sendDraftRequestBundle];
        }

        private IEnumerable<IRequestBundle<RequestInformation>> CreateAttachmentUploadBundles(MimeMessage mime, string mailCopyId, IRequestBase sourceRequest)
        {
            var allAttachments = new List<OutlookFileAttachment>();

            foreach (var part in mime.BodyParts)
            {
                var isAttachmentOrInline = part.IsAttachment ? true : part.ContentDisposition?.Disposition == "inline";

                if (!isAttachmentOrInline) continue;

                using var memory = new MemoryStream();
                ((MimePart)part).Content.DecodeTo(memory);

                var base64String = Convert.ToBase64String(memory.ToArray());

                var attachment = new OutlookFileAttachment()
                {
                    Base64EncodedContentBytes = base64String,
                    FileName = part.ContentDisposition?.FileName ?? part.ContentType.Name,
                    ContentId = part.ContentId,
                    ContentType = part.ContentType.MimeType,
                    IsInline = part.ContentDisposition?.Disposition == "inline"
                };

                allAttachments.Add(attachment);
            }

            RequestInformation PrepareUploadAttachmentRequest(RequestInformation requestInformation, OutlookFileAttachment outlookFileAttachment)
            {
                requestInformation.Headers.Clear();

                string contentJson = JsonSerializer.Serialize(outlookFileAttachment);

                requestInformation.Content = new MemoryStream(Encoding.UTF8.GetBytes(contentJson));
                requestInformation.HttpMethod = Method.POST;
                requestInformation.Headers.Add("Content-Type", "application/json");

                return requestInformation;
            }

            // Prepare attachment upload requests.
            return allAttachments.Select(outlookAttachment =>
            {
                var emptyPostRequest = _graphClient.Me.Messages[mailCopyId].Attachments.ToPostRequestInformation(new Attachment());
                var modifiedAttachmentUploadRequest = PrepareUploadAttachmentRequest(emptyPostRequest, outlookAttachment);

                return new HttpRequestBundle<RequestInformation>(modifiedAttachmentUploadRequest, sourceRequest);
            });
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> Archive(BatchArchiveRequest request)
            => Move(new BatchMoveRequest(request.Items, request.FromFolder, request.ToFolder));

        public override async Task DownloadMissingMimeMessageAsync(IMailItem mailItem,
                                                               MailKit.ITransferProgress transferProgress = null,
                                                               CancellationToken cancellationToken = default)
        {
            var mimeMessage = await DownloadMimeMessageAsync(mailItem.Id, cancellationToken).ConfigureAwait(false);
            await _outlookChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> RenameFolder(RenameFolderRequest request)
        {
            return CreateHttpBundleWithResponse<MailFolder>(request, (item) =>
            {
                if (item is not RenameFolderRequest renameFolderRequest)
                    throw new ArgumentException($"Renaming folder must be handled with '{nameof(RenameFolderRequest)}'");

                var requestBody = new MailFolder
                {
                    DisplayName = request.NewFolderName,
                };

                return _graphClient.Me.MailFolders[request.Folder.RemoteFolderId].ToPatchRequestInformation(requestBody);
            });
        }

        public override IEnumerable<IRequestBundle<RequestInformation>> EmptyFolder(EmptyFolderRequest request)
            => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a))));

        public override IEnumerable<IRequestBundle<RequestInformation>> MarkFolderAsRead(MarkFolderAsReadRequest request)
            => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true)), true));

        #endregion

        public override async Task ExecuteNativeRequestsAsync(IEnumerable<IRequestBundle<RequestInformation>> batchedRequests, CancellationToken cancellationToken = default)
        {
            var batchRequestInformations = BatchExtension.Batch(batchedRequests, (int)MaximumAllowedBatchRequestSize);

            bool serializeRequests = false;

            foreach (var batch in batchRequestInformations)
            {
                var batchContent = new BatchRequestContentCollection(_graphClient);

                var itemCount = batch.Count();

                for (int i = 0; i < itemCount; i++)
                {
                    var bundle = batch.ElementAt(i);

                    if (bundle.Request is BatchRequestBase batchBundleRequest && batchBundleRequest.ExecuteSerialBatch)
                    {
                        // This bundle needs to run every request in serial.
                        // By default requests are executed in parallel.

                        serializeRequests = true;
                    }

                    var request = bundle.Request;
                    var nativeRequest = bundle.NativeRequest;

                    request.ApplyUIChanges();

                    var batchRequestId = await batchContent.AddBatchRequestStepAsync(nativeRequest).ConfigureAwait(false);

                    // Map BundleId to batch request step's key.
                    // This is how we can identify which step succeeded or failed in the bundle.

                    bundle.BundleId = batchRequestId;
                }

                if (!batchContent.BatchRequestSteps.Any())
                    continue;

                // Set execution type to serial instead of parallel if needed.
                // Each step will depend on the previous one.

                if (serializeRequests)
                {
                    for (int i = 1; i < itemCount; i++)
                    {
                        var currentStep = batchContent.BatchRequestSteps.ElementAt(i);
                        var previousStep = batchContent.BatchRequestSteps.ElementAt(i - 1);

                        currentStep.Value.DependsOn = [previousStep.Key];
                    }
                }

                // Execute batch. This will collect responses from network call for each batch step.
                var batchRequestResponse = await _graphClient.Batch.PostAsync(batchContent, cancellationToken).ConfigureAwait(false);

                // Check responses for each bundle id.
                // Each bundle id must return some HttpResponseMessage ideally.

                var bundleIds = batchContent.BatchRequestSteps.Select(a => a.Key);

                var exceptionBag = new List<string>();

                foreach (var bundleId in bundleIds)
                {
                    var bundle = batch.FirstOrDefault(a => a.BundleId == bundleId);

                    if (bundle == null)
                        continue;

                    var httpResponseMessage = await batchRequestResponse.GetResponseByIdAsync(bundleId);

                    if (httpResponseMessage == null)
                        continue;

                    using (httpResponseMessage)
                    {
                        if (!httpResponseMessage.IsSuccessStatusCode)
                        {
                            bundle.Request.RevertUIChanges();

                            var content = await httpResponseMessage.Content.ReadAsStringAsync();
                            var errorJson = JsonObject.Parse(content);
                            var errorString = $"{httpResponseMessage.StatusCode} [{bundle.Request.GetType().Name}]\n{errorJson["error"]["code"]} - {errorJson["error"]["message"]}\n";

                            exceptionBag.Add(errorString);
                        }
                    }
                }

                if (exceptionBag.Any())
                {
                    var formattedErrorString = string.Join("\n", exceptionBag.Select((item, index) => $"{index + 1}. {item}"));

                    throw new SynchronizerException(formattedErrorString);
                }
            }
        }


        private async Task<MimeMessage> DownloadMimeMessageAsync(string messageId, CancellationToken cancellationToken = default)
        {
            var mimeContentStream = await _graphClient.Me.Messages[messageId].Content.GetAsync(null, cancellationToken).ConfigureAwait(false);
            return await MimeMessage.LoadAsync(mimeContentStream).ConfigureAwait(false);
        }

        public override async Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(Message message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
        {

            var mimeMessage = await DownloadMimeMessageAsync(message.Id, cancellationToken).ConfigureAwait(false);
            var mailCopy = message.AsMailCopy();

            if (message.IsDraft.GetValueOrDefault()
                && mimeMessage.Headers.Contains(Domain.Constants.WinoLocalDraftHeader)
                && Guid.TryParse(mimeMessage.Headers[Domain.Constants.WinoLocalDraftHeader], out Guid localDraftCopyUniqueId))
            {
                // This message belongs to existing local draft copy.
                // We don't need to create a new mail copy for this message, just update the existing one.

                bool isMappingSuccessful = await _outlookChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopyUniqueId, mailCopy.Id, mailCopy.DraftId, mailCopy.ThreadId);

                if (isMappingSuccessful) return null;

                // Local copy doesn't exists. Continue execution to insert mail copy.
            }

            // Outlook messages can only be assigned to 1 folder at a time.
            // Therefore we don't need to create multiple copies of the same message for different folders.
            var package = new NewMailItemPackage(mailCopy, mimeMessage, assignedFolder.RemoteFolderId);

            return [package];
        }
    }
}

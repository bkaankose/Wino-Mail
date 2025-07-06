using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using MimeKit;
using MoreLinq.Extensions;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Errors;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Http;
using Wino.Core.Integration.Processors;
using Wino.Core.Misc;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;

namespace Wino.Core.Synchronizers.Mail;

[JsonSerializable(typeof(Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody))]
[JsonSerializable(typeof(OutlookFileAttachment))]
public partial class OutlookSynchronizerJsonContext : JsonSerializerContext;

public class OutlookSynchronizer : WinoSynchronizer<RequestInformation, Message, Event>
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
    private readonly IOutlookSynchronizerErrorHandlerFactory _errorHandlingFactory;

    public OutlookSynchronizer(MailAccount account,
                               IAuthenticator authenticator,
                               IOutlookChangeProcessor outlookChangeProcessor,
                               IOutlookSynchronizerErrorHandlerFactory errorHandlingFactory) : base(account)
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
        _errorHandlingFactory = errorHandlingFactory;
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


    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();

        _logger.Information("Internal synchronization started for {Name}", Account.Name);
        _logger.Information("Options: {Options}", options);

        try
        {
            PublishSynchronizationProgress(1);

            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);

            if (options.Type != MailSynchronizationType.FoldersOnly)
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

        return MailSynchronizationResult.Completed(unreadNewItems);
    }

    public async Task DownloadSearchResultMessageAsync(string messageId, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        Log.Information("Downloading search result message {messageId} for {Name} - {FolderName}", messageId, Account.Name, assignedFolder.FolderName);

        // Outlook message handling was a little strange.
        // Instead of changing it from the scratch, we will just download the message and process it.
        // Search results will only return Id for the messages.
        // This method will download the raw mime, get the required enough metadata from the service and create
        // the mail locally. Message ids passed to this method is expected to be non-existent locally.

        var message = await _graphClient.Me.Messages[messageId].GetAsync((config) =>
        {
            config.QueryParameters.Select = outlookMessageSelectParameters;
        }, cancellationToken).ConfigureAwait(false);

        var mailPackages = await CreateNewMailPackagesAsync(message, assignedFolder, cancellationToken).ConfigureAwait(false);

        if (mailPackages == null) return;

        foreach (var package in mailPackages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _outlookChangeProcessor.CreateMailRawAsync(Account, assignedFolder, package).ConfigureAwait(false);
        }
    }

    private async Task<IEnumerable<string>> SynchronizeFolderAsync(MailItemFolder folder, CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();

        cancellationToken.ThrowIfCancellationRequested();

    retry:
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

            try
            {
                messageCollectionPage = await _graphClient.RequestAdapter.SendAsync(requestInformation, Microsoft.Graph.Me.MailFolders.Item.Messages.Delta.DeltaGetResponse.CreateFromDiscriminatorValue, cancellationToken: cancellationToken);
            }
            catch (ApiException apiException) when (apiException.ResponseStatusCode == 410)
            {
                folder.DeltaToken = string.Empty;

                goto retry;
            }
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

    /// <summary>
    /// Somehow, Graph API returns Message type item for items like TodoTask, EventMessage and Contact.
    /// Basically deleted item retention items are stored as Message object in Deleted Items folder.
    /// Suprisingly, odatatype will also be the same as Message.
    /// In order to differentiate them from regular messages, we need to check the addresses in the message.
    /// </summary>
    /// <param name="item">Retrieved message.</param>
    /// <returns>Whether the item is non-Message type or not.</returns>
    private bool IsNotRealMessageType(Message item)
        => item is EventMessage || item.From?.EmailAddress == null;

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
                if (IsNotRealMessageType(item))
                {
                    if (item is EventMessage eventMessage)
                    {
                        Log.Warning("Recieved event message. This is not supported yet. {Id}", eventMessage.Id);
                    }
                    else
                    {
                        Log.Warning("Recieved either contact or todo item as message This is not supported yet. {Id}", item.Id);
                    }

                    return true;
                }

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
        var specialFolderInfo = await GetSpecialFolderIdsAsync(cancellationToken).ConfigureAwait(false);
        var graphFolders = await GetDeltaFoldersAsync(cancellationToken).ConfigureAwait(false);

        var iterator = PageIterator<MailFolder, Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse>
            .CreatePageIterator(_graphClient, graphFolders, (folder) =>
                HandleFolderRetrievedAsync(folder, specialFolderInfo, cancellationToken));

        await iterator.IterateAsync();

        await UpdateDeltaSynchronizationIdentifierAsync(iterator.Deltalink).ConfigureAwait(false);
    }

    [RequiresUnreferencedCode("Calls Microsoft.Kiota.Abstractions.Serialization.KiotaJsonSerializer.DeserializeAsync<T>(String, CancellationToken)")]
    private async Task<T> DeserializeGraphBatchResponseAsync<T>(BatchResponseContentCollection collection, string requestId, CancellationToken cancellationToken = default) where T : IParsable, new()
    {
        // This deserialization may throw generalException in case of failure.
        // Bug: https://github.com/microsoftgraph/msgraph-sdk-dotnet/issues/2010
        // This is a workaround for the bug to retrieve the actual exception.
        // All generic batch response deserializations must go under this method.

        try
        {
            return await collection.GetResponseByIdAsync<T>(requestId);
        }
        catch (ODataError)
        {
            throw;
        }
        catch (ServiceException serviceException)
        {
            // Actual exception is hidden inside ServiceException.


            ODataError errorResult = await KiotaJsonSerializer.DeserializeAsync<ODataError>(serviceException.RawResponseBody, cancellationToken);

            throw new SynchronizerException("Outlook Error", errorResult);
        }
    }

    private async Task<OutlookSpecialFolderIdInformation> GetSpecialFolderIdsAsync(CancellationToken cancellationToken)
    {
        var wellKnownFolderIdBatch = new BatchRequestContentCollection(_graphClient);
        var folderRequests = new Dictionary<string, RequestInformation>
        {
            { INBOX_NAME, _graphClient.Me.MailFolders[INBOX_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; }) },
            { SENT_NAME, _graphClient.Me.MailFolders[SENT_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; }) },
            { DELETED_NAME, _graphClient.Me.MailFolders[DELETED_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; }) },
            { JUNK_NAME, _graphClient.Me.MailFolders[JUNK_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; }) },
            { DRAFTS_NAME, _graphClient.Me.MailFolders[DRAFTS_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; }) },
            { ARCHIVE_NAME, _graphClient.Me.MailFolders[ARCHIVE_NAME].ToGetRequestInformation((t) => { t.QueryParameters.Select = ["id"]; }) }
        };

        var batchIds = new Dictionary<string, string>();
        foreach (var request in folderRequests)
        {
            batchIds[request.Key] = await wellKnownFolderIdBatch.AddBatchRequestStepAsync(request.Value);
        }

        var returnedResponse = await _graphClient.Batch.PostAsync(wellKnownFolderIdBatch, cancellationToken).ConfigureAwait(false);

        var folderIds = new Dictionary<string, string>();
        foreach (var batchId in batchIds)
        {
            folderIds[batchId.Key] = (await DeserializeGraphBatchResponseAsync<MailFolder>(returnedResponse, batchId.Value, cancellationToken)).Id;
        }

        return new OutlookSpecialFolderIdInformation(
            folderIds[INBOX_NAME],
            folderIds[DELETED_NAME],
            folderIds[JUNK_NAME],
            folderIds[DRAFTS_NAME],
            folderIds[SENT_NAME],
            folderIds[ARCHIVE_NAME]);
    }

    private async Task<Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse> GetDeltaFoldersAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier))
        {
            var deltaRequest = _graphClient.Me.MailFolders.Delta.ToGetRequestInformation();
            deltaRequest.UrlTemplate = deltaRequest.UrlTemplate.Insert(deltaRequest.UrlTemplate.Length - 1, ",includehiddenfolders");
            deltaRequest.QueryParameters.Add("includehiddenfolders", "true");

            return await _graphClient.RequestAdapter.SendAsync(deltaRequest,
                Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse.CreateFromDiscriminatorValue,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var deltaRequest = _graphClient.Me.MailFolders.Delta.ToGetRequestInformation();
            deltaRequest.UrlTemplate = deltaRequest.UrlTemplate.Insert(deltaRequest.UrlTemplate.Length - 1, ",%24deltaToken");
            deltaRequest.QueryParameters.Add("%24deltaToken", Account.SynchronizationDeltaIdentifier);

            return await _graphClient.RequestAdapter.SendAsync(deltaRequest,
                Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse.CreateFromDiscriminatorValue,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException apiException) when (apiException.ResponseStatusCode == 410)
        {
            Account.SynchronizationDeltaIdentifier = string.Empty;
            return await GetDeltaFoldersAsync(cancellationToken);
        }
    }

    private async Task UpdateDeltaSynchronizationIdentifierAsync(string deltalink)
    {
        if (string.IsNullOrEmpty(deltalink)) return;

        var deltaToken = deltalink.Split('=')[1];
        var latestAccountDeltaToken = await _outlookChangeProcessor
            .UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, deltaToken);

        if (!string.IsNullOrEmpty(latestAccountDeltaToken))
        {
            Account.SynchronizationDeltaIdentifier = latestAccountDeltaToken;
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
            // Don't throw for profile picture.
            // Office 365 apps require different permissions for profile picture.
            // This permission requires admin consent.
            // We avoid those permissions for now.

            return string.Empty;
        }
    }

    /// <summary>
    /// Get the user's display name.
    /// </summary>
    /// <returns>Display name and address of the user.</returns>
    private async Task<Tuple<string, string>> GetDisplayNameAndAddressAsync()
    {
        var userInfo = await _graphClient.Me.GetAsync();

        return new Tuple<string, string>(userInfo.DisplayName, userInfo.Mail);
    }

    public override async Task<ProfileInformation> GetProfileInformationAsync()
    {
        var profilePictureData = await GetUserProfilePictureAsync().ConfigureAwait(false);
        var displayNameAndAddress = await GetDisplayNameAndAddressAsync().ConfigureAwait(false);

        return new ProfileInformation(displayNameAndAddress.Item1, profilePictureData, displayNameAndAddress.Item2);
    }

    /// <summary>
    /// POST requests are handled differently in batches in Graph SDK.
    /// Batch basically ignores the step's coontent-type and body.
    /// Manually create a POST request with empty body and send it.
    /// </summary>
    /// <param name="requestInformation">Post request information.</param>
    /// <param name="content">Content object to serialize.</param>
    /// <returns>Updated post request information.</returns>
    private RequestInformation PreparePostRequestInformation(RequestInformation requestInformation, Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody content = null)
    {
        requestInformation.Headers.Clear();

        string contentJson = content == null ? "{}" : JsonSerializer.Serialize(content, OutlookSynchronizerJsonContext.Default.MovePostRequestBody);

        requestInformation.Content = new MemoryStream(Encoding.UTF8.GetBytes(contentJson));
        requestInformation.HttpMethod = Method.POST;
        requestInformation.Headers.Add("Content-Type", "application/json");

        return requestInformation;
    }

    #region Mail Integration

    public override bool DelaySendOperationSynchronization() => true;

    public override List<IRequestBundle<RequestInformation>> Move(BatchMoveRequest request)
    {
        return ForEachRequest(request, (item) =>
        {
            var requestBody = new Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody()
            {
                DestinationId = item.ToFolder.RemoteFolderId
            };

            return PreparePostRequestInformation(_graphClient.Me.Messages[item.Item.Id].Move.ToPostRequestInformation(requestBody),
                                                                 requestBody);
        });
    }

    public override List<IRequestBundle<RequestInformation>> ChangeFlag(BatchChangeFlagRequest request)
    {
        return ForEachRequest(request, (item) =>
        {
            var message = new Message()
            {
                Flag = new FollowupFlag() { FlagStatus = item.IsFlagged ? FollowupFlagStatus.Flagged : FollowupFlagStatus.NotFlagged }
            };

            return _graphClient.Me.Messages[item.Item.Id].ToPatchRequestInformation(message);
        });
    }

    public override List<IRequestBundle<RequestInformation>> MarkRead(BatchMarkReadRequest request)
    {
        return ForEachRequest(request, (item) =>
        {
            var message = new Message()
            {
                IsRead = item.IsRead
            };

            return _graphClient.Me.Messages[item.Item.Id].ToPatchRequestInformation(message);
        });
    }

    public override List<IRequestBundle<RequestInformation>> Delete(BatchDeleteRequest request)
    {
        return ForEachRequest(request, (item) =>
        {
            return _graphClient.Me.Messages[item.Item.Id].ToDeleteRequestInformation();
        });
    }

    public override List<IRequestBundle<RequestInformation>> MoveToFocused(BatchMoveToFocusedRequest request)
    {
        return ForEachRequest(request, (item) =>
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

    public override List<IRequestBundle<RequestInformation>> AlwaysMoveTo(BatchAlwaysMoveToRequest request)
    {
        return ForEachRequest(request, (item) =>
        {
            var inferenceClassificationOverride = new InferenceClassificationOverride
            {
                ClassifyAs = item.MoveToFocused ? InferenceClassificationType.Focused : InferenceClassificationType.Other,
                SenderEmailAddress = new EmailAddress
                {
                    Name = item.Item.FromName,
                    Address = item.Item.FromAddress
                }
            };

            return _graphClient.Me.InferenceClassification.Overrides.ToPostRequestInformation(inferenceClassificationOverride);
        });
    }

    public override List<IRequestBundle<RequestInformation>> CreateDraft(CreateDraftRequest createDraftRequest)
    {
        var reason = createDraftRequest.DraftPreperationRequest.Reason;
        var message = createDraftRequest.DraftPreperationRequest.CreatedLocalDraftMimeMessage.AsOutlookMessage(true);

        if (reason == DraftCreationReason.Empty)
        {
            return [new HttpRequestBundle<RequestInformation>(_graphClient.Me.Messages.ToPostRequestInformation(message), createDraftRequest)];
        }
        else if (reason == DraftCreationReason.Reply)
        {
            return [new HttpRequestBundle<RequestInformation>(_graphClient.Me.Messages[createDraftRequest.DraftPreperationRequest.ReferenceMailCopy.Id].CreateReply.ToPostRequestInformation(new Microsoft.Graph.Me.Messages.Item.CreateReply.CreateReplyPostRequestBody()
            {
                Message = message
            }), createDraftRequest)];

        }
        else if (reason == DraftCreationReason.ReplyAll)
        {
            return [new HttpRequestBundle<RequestInformation>(_graphClient.Me.Messages[createDraftRequest.DraftPreperationRequest.ReferenceMailCopy.Id].CreateReplyAll.ToPostRequestInformation(new Microsoft.Graph.Me.Messages.Item.CreateReplyAll.CreateReplyAllPostRequestBody()
            {
                Message = message
            }), createDraftRequest)];
        }
        else if (reason == DraftCreationReason.Forward)
        {
            return [new HttpRequestBundle<RequestInformation>( _graphClient.Me.Messages[createDraftRequest.DraftPreperationRequest.ReferenceMailCopy.Id].CreateForward.ToPostRequestInformation(new Microsoft.Graph.Me.Messages.Item.CreateForward.CreateForwardPostRequestBody()
            {
                Message = message
            }), createDraftRequest)];
        }
        else
        {
            throw new NotImplementedException("Draft creation reason is not implemented.");
        }
    }

    public override List<IRequestBundle<RequestInformation>> SendDraft(SendDraftRequest request)
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

    private List<IRequestBundle<RequestInformation>> CreateAttachmentUploadBundles(MimeMessage mime, string mailCopyId, IRequestBase sourceRequest)
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

        static RequestInformation PrepareUploadAttachmentRequest(RequestInformation requestInformation, OutlookFileAttachment outlookFileAttachment)
        {
            requestInformation.Headers.Clear();

            string contentJson = JsonSerializer.Serialize(outlookFileAttachment, OutlookSynchronizerJsonContext.Default.OutlookFileAttachment);

            requestInformation.Content = new MemoryStream(Encoding.UTF8.GetBytes(contentJson));
            requestInformation.HttpMethod = Method.POST;
            requestInformation.Headers.Add("Content-Type", "application/json");

            return requestInformation;
        }

        var retList = new List<IRequestBundle<RequestInformation>>();

        // Prepare attachment upload requests.

        foreach (var attachment in allAttachments)
        {
            var emptyPostRequest = _graphClient.Me.Messages[mailCopyId].Attachments.ToPostRequestInformation(new Attachment());
            var modifiedAttachmentUploadRequest = PrepareUploadAttachmentRequest(emptyPostRequest, attachment);

            var bundle = new HttpRequestBundle<RequestInformation>(modifiedAttachmentUploadRequest, null);

            retList.Add(bundle);
        }

        return retList;
    }

    public override List<IRequestBundle<RequestInformation>> Archive(BatchArchiveRequest request)
    {
        var batchMoveRequest = new BatchMoveRequest(request.Select(item => new MoveRequest(item.Item, item.FromFolder, item.ToFolder)));

        return Move(batchMoveRequest);
    }

    public override async Task DownloadMissingMimeMessageAsync(IMailItem mailItem,
                                                           MailKit.ITransferProgress transferProgress = null,
                                                           CancellationToken cancellationToken = default)
    {
        var mimeMessage = await DownloadMimeMessageAsync(mailItem.Id, cancellationToken).ConfigureAwait(false);
        await _outlookChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
    }

    public override List<IRequestBundle<RequestInformation>> RenameFolder(RenameFolderRequest request)
    {
        var requestBody = new MailFolder
        {
            DisplayName = request.NewFolderName,
        };

        var networkCall = _graphClient.Me.MailFolders[request.Folder.RemoteFolderId].ToPatchRequestInformation(requestBody);

        return [new HttpRequestBundle<RequestInformation>(networkCall, request)];
    }

    public override List<IRequestBundle<RequestInformation>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a))));

    public override List<IRequestBundle<RequestInformation>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    #endregion

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<RequestInformation>> batchedRequests, CancellationToken cancellationToken = default)
    {
        var batchedGroups = batchedRequests.Batch((int)MaximumAllowedBatchRequestSize);

        foreach (var batch in batchedGroups)
        {
            await ExecuteBatchRequestsAsync(batch, cancellationToken);
        }
    }

    private async Task ExecuteBatchRequestsAsync(IEnumerable<IRequestBundle<RequestInformation>> batch, CancellationToken cancellationToken)
    {
        var batchContent = new BatchRequestContentCollection(_graphClient);
        var itemCount = batch.Count();

        if (itemCount == 0) return;

        var bundleIdMap = await PrepareBatchContentAsync(batch, batchContent, itemCount);

        // Execute batch to collect responses from network call
        var batchRequestResponse = await _graphClient.Batch.PostAsync(batchContent, cancellationToken);

        await ProcessBatchResponsesAsync(batch, batchRequestResponse, bundleIdMap);
    }

    private async Task<Dictionary<string, IRequestBundle<RequestInformation>>> PrepareBatchContentAsync(
        IEnumerable<IRequestBundle<RequestInformation>> batch,
        BatchRequestContentCollection batchContent,
        int itemCount)
    {
        var bundleIdMap = new Dictionary<string, IRequestBundle<RequestInformation>>();
        bool requiresSerial = false;

        for (int i = 0; i < itemCount; i++)
        {
            var bundle = batch.ElementAt(i);
            requiresSerial |= bundle.UIChangeRequest is SendDraftRequest;

            bundle.UIChangeRequest?.ApplyUIChanges();
            var batchRequestId = await batchContent.AddBatchRequestStepAsync(bundle.NativeRequest);
            bundle.BundleId = batchRequestId;
            bundleIdMap[batchRequestId] = bundle;
        }

        if (requiresSerial)
        {
            ConfigureSerialExecution(batchContent);
        }

        return bundleIdMap;
    }

    private void ConfigureSerialExecution(BatchRequestContentCollection batchContent)
    {
        // Set each step to depend on previous one for serial execution
        var steps = batchContent.BatchRequestSteps.ToList();
        for (int i = 1; i < steps.Count; i++)
        {
            var currentStep = steps[i].Value;
            var previousStepKey = steps[i - 1].Key;
            currentStep.DependsOn = [previousStepKey];
        }
    }

    private async Task ProcessBatchResponsesAsync(
        IEnumerable<IRequestBundle<RequestInformation>> batch,
        BatchResponseContentCollection batchResponse,
        Dictionary<string, IRequestBundle<RequestInformation>> bundleIdMap)
    {
        var errors = new List<string>();

        foreach (var bundleId in bundleIdMap.Keys)
        {
            var bundle = bundleIdMap[bundleId];
            var response = await batchResponse.GetResponseByIdAsync(bundleId);

            if (response == null) continue;

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    await HandleFailedResponseAsync(bundle, response, errors);
                }
            }
        }

        if (errors.Any())
        {
            ThrowBatchExecutionException(errors);
        }
    }

    private async Task HandleFailedResponseAsync(
        IRequestBundle<RequestInformation> bundle,
        HttpResponseMessage response,
        List<string> errors)
    {
        var content = await response.Content.ReadAsStringAsync();
        var errorJson = JsonNode.Parse(content);
        var errorCode = errorJson["error"]["code"].GetValue<string>();
        var errorMessage = errorJson["error"]["message"].GetValue<string>();
        var errorString = $"[{response.StatusCode}] {errorCode} - {errorMessage}\n";

        // Create error context
        var errorContext = new SynchronizerErrorContext
        {
            Account = Account,
            ErrorCode = (int)response.StatusCode,
            ErrorMessage = errorMessage,
            RequestBundle = bundle,
            AdditionalData = new Dictionary<string, object>
            {
                { "ErrorCode", errorCode },
                { "HttpResponse", response },
                { "Content", content }
            }
        };

        // Try to handle the error with registered handlers
        var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext);

        // If not handled by any specific handler, revert UI changes and add to error list
        if (!handled)
        {
            bundle.UIChangeRequest?.RevertUIChanges();
            Debug.WriteLine(errorString);
            errors.Add(errorString);
        }
    }

    private void ThrowBatchExecutionException(List<string> errors)
    {
        var formattedErrorString = string.Join("\n",
            errors.Select((item, index) => $"{index + 1}. {item}"));
        throw new SynchronizerException(formattedErrorString);
    }

    public override async Task<List<MailCopy>> OnlineSearchAsync(string queryText, List<IMailItemFolder> folders, CancellationToken cancellationToken = default)
    {
        List<Message> messagesReturnedByApi = [];

        // Perform search for each folder separately.
        if (folders?.Count > 0)
        {
            var folderIds = folders.Select(a => a.RemoteFolderId);

            var tasks = folderIds.Select(async folderId =>
            {
                var mailQuery = _graphClient.Me.MailFolders[folderId].Messages
                    .GetAsync(requestConfig =>
                    {
                        requestConfig.QueryParameters.Search = $"\"{queryText}\"";
                        requestConfig.QueryParameters.Select = ["Id, ParentFolderId"];
                        requestConfig.QueryParameters.Top = 1000;
                    });

                var result = await mailQuery;

                if (result?.Value != null)
                {
                    lock (messagesReturnedByApi)
                    {
                        messagesReturnedByApi.AddRange(result.Value);
                    }
                }
            });

            await Task.WhenAll(tasks);
        }
        else
        {
            // Perform search for all messages without folder data.
            var mailQuery = _graphClient.Me.Messages
                .GetAsync(requestConfig =>
                {
                    requestConfig.QueryParameters.Search = $"\"{queryText}\"";
                    requestConfig.QueryParameters.Select = ["Id, ParentFolderId"];
                    requestConfig.QueryParameters.Top = 1000;
                }, cancellationToken);

            var result = await mailQuery;

            if (result?.Value != null)
            {
                messagesReturnedByApi.AddRange(result.Value);
            }
        }

        if (messagesReturnedByApi.Count == 0) return [];

        var localFolders = (await _outlookChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false))
            .ToDictionary(x => x.RemoteFolderId);

        var messagesDictionary = messagesReturnedByApi.ToDictionary(a => a.Id);

        // Contains a list of message ids that potentially can be downloaded.
        List<string> messageIdsWithKnownFolder = [];

        // Validate that all messages are in a known folder.
        foreach (var message in messagesReturnedByApi)
        {
            if (!localFolders.ContainsKey(message.ParentFolderId))
            {
                Log.Warning("Search result returned a message from a folder that is not synchronized.");
                continue;
            }

            messageIdsWithKnownFolder.Add(message.Id);
        }

        var locallyExistingMails = await _outlookChangeProcessor.AreMailsExistsAsync(messageIdsWithKnownFolder).ConfigureAwait(false);

        // Find messages that are not downloaded yet.
        List<Message> messagesToDownload = [];
        foreach (var id in messagesDictionary.Keys.Except(locallyExistingMails))
        {
            messagesToDownload.Add(messagesDictionary[id]);
        }

        foreach (var message in messagesToDownload)
        {
            await DownloadSearchResultMessageAsync(message.Id, localFolders[message.ParentFolderId], cancellationToken).ConfigureAwait(false);
        }

        // Get results from database and return.
        return await _outlookChangeProcessor.GetMailCopiesAsync(messageIdsWithKnownFolder).ConfigureAwait(false);
    }

    private async Task<MimeMessage> DownloadMimeMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var mimeContentStream = await _graphClient.Me.Messages[messageId].Content.GetAsync(null, cancellationToken).ConfigureAwait(false);
        return await MimeMessage.LoadAsync(mimeContentStream, cancellationToken).ConfigureAwait(false);
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

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        _logger.Information("Internal calendar synchronization started for {Name}", Account.Name);

        cancellationToken.ThrowIfCancellationRequested();

        await SynchronizeCalendarsAsync(cancellationToken).ConfigureAwait(false);

        var localCalendars = await _outlookChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        foreach (var calendar in localCalendars)
        {
            bool isInitialSync = string.IsNullOrEmpty(calendar.SynchronizationDeltaToken);

            if (isInitialSync)
            {
                await FullSynchronizeCalendarEventsAsync(calendar);
            }
            else
            {
                await DeltaSynchronizeCalendarAsync(calendar);
            }
        }

        return default;
    }

    /// <summary>
    /// Checks if the token is a time-based token (old format) rather than a delta token
    /// </summary>
    /// <param name="token">The token to check</param>
    /// <returns>True if it's a time-based token</returns>
    private bool IsTimeBasedToken(string token)
    {
        // Time-based tokens are ISO 8601 datetime strings
        return DateTime.TryParse(token, out _);
    }

    /// <summary>
    /// Executes a delta query using the provided delta URL
    /// </summary>
    /// <param name="deltaUrl">The delta URL from previous sync</param>
    /// <returns>Event collection response</returns>
    private async Task<EventCollectionResponse?> ExecuteDeltaQueryAsync(string deltaUrl)
    {
        try
        {
            // Create a custom request using the delta URL
            var requestInfo = new RequestInformation
            {
                HttpMethod = Method.GET,
                URI = new Uri(deltaUrl)
            };

            // Add required headers
            requestInfo.Headers.Add("Accept", "application/json");

            var response = await _graphClient.RequestAdapter.SendAsync(requestInfo, EventCollectionResponse.CreateFromDiscriminatorValue);
            return response;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error executing delta query: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Extracts the delta token from the @odata.deltaLink property in the response
    /// </summary>
    /// <param name="response">The event collection response</param>
    /// <returns>The delta token URL or null if not found</returns>
    private string? ExtractDeltaTokenFromResponse(EventCollectionResponse? response)
    {
        try
        {
            if (response?.AdditionalData?.ContainsKey("@odata.deltaLink") == true)
            {
                return response.AdditionalData["@odata.deltaLink"]?.ToString();
            }

            // Check for nextLink first, then deltaLink
            if (response?.OdataNextLink != null)
            {
                return response.OdataNextLink;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting delta token: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Processes pagination for delta events and continues until deltaLink is reached
    /// </summary>
    /// <param name="calendarId">The database calendar ID</param>
    /// <param name="initialResponse">The initial delta response</param>
    private async Task ProcessDeltaEventsPaginationAsync(Guid calendarId, EventCollectionResponse? initialResponse, string? outlookCalendarId = null)
    {
        try
        {
            var currentResponse = initialResponse;

            while (!string.IsNullOrEmpty(currentResponse?.OdataNextLink))
            {
                Console.WriteLine($"   📃 Processing next page of delta events...");

                // Get next page
                currentResponse = await ExecuteDeltaQueryAsync(currentResponse.OdataNextLink);

                var events = currentResponse?.Value ?? new List<Microsoft.Graph.Models.Event>();

                foreach (var outlookEvent in events)
                {
                    await ProcessOutlookDeltaEventAsync(calendarId, outlookEvent, outlookCalendarId);
                }
            }

            Console.WriteLine($"   ✅ Completed processing all delta event pages");

            // Update the delta token from the final response
            if (currentResponse != null)
            {
                var finalDeltaToken = ExtractDeltaTokenFromResponse(currentResponse);
                if (!string.IsNullOrEmpty(finalDeltaToken) && !string.IsNullOrEmpty(outlookCalendarId))
                {
                    var calendarRemoteId = $"{outlookCalendarId}";
                    await _outlookChangeProcessor.UpdateCalendarSyncTokenAsync(calendarRemoteId, finalDeltaToken);
                    Console.WriteLine($"   🔄 Updated delta token for next sync");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error processing delta events pagination: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Extracts delta token from delta initialization response
    /// </summary>
    /// <param name="response">The delta response</param>
    /// <returns>The delta token or null</returns>
    private string? ExtractDeltaTokenFromInitResponse(Microsoft.Graph.Me.Calendars.Item.Events.Delta.DeltaGetResponse? response)
    {
        try
        {
            Console.WriteLine($"   🔍 Extracting delta token from init response...");

            if (!string.IsNullOrEmpty(response?.OdataDeltaLink))
            {
                return response?.OdataDeltaLink;
            }

            if (response?.AdditionalData?.ContainsKey("@odata.deltaLink") == true)
            {
                var deltaLink = response.AdditionalData["@odata.deltaLink"]?.ToString();
                Console.WriteLine($"   📄 Found @odata.deltaLink: {deltaLink}");
                return deltaLink;
            }

            if (response?.OdataNextLink != null)
            {
                Console.WriteLine($"   📄 Found @odata.nextLink: {response.OdataNextLink}");
                return response.OdataNextLink;
            }

            Console.WriteLine($"   ⚠️ No delta or next link found in response");
            if (response?.AdditionalData != null)
            {
                Console.WriteLine($"   📋 Available additional data keys: {string.Join(", ", response.AdditionalData.Keys)}");
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting delta token from init response: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Processes pagination during delta initialization
    /// </summary>
    /// <param name="calendarId">The database calendar ID</param>
    /// <param name="initialResponse">The initial delta response</param>
    /// <param name="outlookCalendarId">The Outlook calendar ID</param>
    private async Task ProcessDeltaInitializationPaginationAsync(Guid calendarId, Microsoft.Graph.Me.Calendars.Item.Events.Delta.DeltaGetResponse? initialResponse, string outlookCalendarId)
    {
        try
        {
            if (initialResponse == null)
            {
                Console.WriteLine($"   ⚠️ No initial response for pagination");
                return;
            }

            Console.WriteLine($"   🔄 Processing pagination for delta initialization...");

            // Process all events through pagination
            var currentResponse = initialResponse;

            // Process initial page events
            if (currentResponse.Value != null)
            {
                foreach (var outlookEvent in currentResponse.Value)
                {
                    await SynchronizeEventAsync(calendarId, outlookEvent);
                }
            }

            // Continue pagination if there are more pages
            while (!string.IsNullOrEmpty(currentResponse?.OdataNextLink))
            {
                Console.WriteLine($"   📃 Processing next page of initialization events...");

                // Create a request for the next page URL
                var requestInfo = new RequestInformation
                {
                    HttpMethod = Method.GET,
                    URI = new Uri(currentResponse.OdataNextLink)
                };
                requestInfo.Headers.Add("Accept", "application/json");

                // Get next page as DeltaGetResponse
                currentResponse = await _graphClient.RequestAdapter.SendAsync(requestInfo, Microsoft.Graph.Me.Calendars.Item.Events.Delta.DeltaGetResponse.CreateFromDiscriminatorValue);

                // Process events from this page
                if (currentResponse?.Value != null)
                {
                    foreach (var outlookEvent in currentResponse.Value)
                    {
                        await SynchronizeEventAsync(calendarId, outlookEvent);
                    }
                }
            }

            // Now extract delta token from the FINAL response (after all pagination)
            var deltaToken = ExtractDeltaTokenFromInitResponse(currentResponse);
            if (!string.IsNullOrEmpty(deltaToken))
            {
                await _outlookChangeProcessor.UpdateCalendarSyncTokenAsync($"{outlookCalendarId}", deltaToken);
                Console.WriteLine($"   🎯 Delta token established for future incremental syncs: {deltaToken?.Substring(0, Math.Min(50, deltaToken.Length))}...");
            }
            else
            {
                Console.WriteLine($"   ⚠️ No delta token received - will retry on next sync");
            }

            Console.WriteLine("   ✅ Completed processing all initialization pages");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error processing delta initialization pagination: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Initializes an Outlook calendar with full sync and establishes delta token
    /// </summary>
    /// <param name="outlookCalendarId">The Outlook calendar ID to initialize</param>
    private async Task InitializeOutlookCalendarWithDeltaTokenAsync(string outlookCalendarId)
    {
        try
        {
            Console.WriteLine($"   🔄 Initializing delta sync for calendar: {outlookCalendarId}");

            // Get the database calendar
            var dbCalendar = await _outlookChangeProcessor.GetCalendarByRemoteIdAsync($"{outlookCalendarId}");
            if (dbCalendar == null)
            {
                Console.WriteLine($"   ❌ Database calendar not found: {outlookCalendarId}");
                return;
            }

            // Perform initial delta query to get baseline and delta token
            // Use custom request to avoid problematic query parameters
            Console.WriteLine($"   🔍 Making clean delta request...");

            // Build a clean delta URL without problematic query parameters
            var deltaUrl = $"https://graph.microsoft.com/v1.0/me/calendars/{outlookCalendarId}/events/delta";
            Console.WriteLine($"   🔍 Clean delta request URL: {deltaUrl}");

            // Execute clean delta request
            var requestInfo = new RequestInformation
            {
                HttpMethod = Method.GET,
                URI = new Uri(deltaUrl)
            };
            requestInfo.Headers.Add("Accept", "application/json");

            var initialDeltaResponse = await _graphClient.RequestAdapter.SendAsync(requestInfo, Microsoft.Graph.Me.Calendars.Item.Events.Delta.DeltaGetResponse.CreateFromDiscriminatorValue);

            var allEvents = initialDeltaResponse?.Value ?? new List<Microsoft.Graph.Models.Event>();

            if (allEvents.Count > 0)
            {
                Console.WriteLine($"   📥 Processing {allEvents.Count} events during initialization...");

                foreach (var outlookEvent in allEvents)
                {
                    await SynchronizeEventAsync(dbCalendar.Id, outlookEvent);
                }
            }

            // Process all pages to get to the deltaLink
            await ProcessDeltaInitializationPaginationAsync(dbCalendar.Id, initialDeltaResponse, outlookCalendarId);

            Console.WriteLine($"   🎯 Delta token initialization completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Failed to initialize delta sync for calendar {outlookCalendarId}: {ex.Message}");
            throw;
        }
    }

    private async Task FullSynchronizeCalendarEventsAsync(AccountCalendar calendar)
    {
        var outlookCalendarId = calendar.RemoteCalendarId;
        try
        {
            Console.WriteLine($"   🔄 Full sync for calendar: {outlookCalendarId}");

            // Get the database calendar
            var dbCalendar = await _outlookChangeProcessor.GetCalendarByRemoteIdAsync($"{outlookCalendarId}");
            if (dbCalendar == null)
            {
                Console.WriteLine($"   ❌ Database calendar not found: {outlookCalendarId}");
                return;
            }

            // Step 1: Perform initial delta query to get all events and establish baseline
            Console.WriteLine($"   📥 Fetching all events using delta endpoint...");

            // Use the delta endpoint to get all events - this establishes the initial state
            // IMPORTANT: We must include includeDeletedEvents=true even in the initial query
            // to ensure the delta token supports deleted events in subsequent calls
            var requestUrl = $"https://graph.microsoft.com/v1.0/me/calendars/{outlookCalendarId}/events/delta?includeDeletedEvents=true";
            var requestInfo = new RequestInformation
            {
                HttpMethod = Method.GET,
                URI = new Uri(requestUrl)
            };
            requestInfo.Headers.Add("Accept", "application/json");

            var deltaRequest = await _graphClient.RequestAdapter.SendAsync(requestInfo, Microsoft.Graph.Me.Calendars.Item.Events.Delta.DeltaGetResponse.CreateFromDiscriminatorValue);

            var allEvents = deltaRequest?.Value ?? new List<Microsoft.Graph.Models.Event>();
            Console.WriteLine($"   📋 Processing {allEvents.Count} events from initial delta response...");

            // Process all events from the initial response
            foreach (var outlookEvent in allEvents)
            {
                await ProcessOutlookDeltaEventAsync(calendar.Id, outlookEvent, outlookCalendarId);
            }

            // Step 2: Process pagination until we reach the deltaLink
            var currentResponse = deltaRequest;
            while (!string.IsNullOrEmpty(currentResponse?.OdataNextLink))
            {
                Console.WriteLine($"   📄 Processing next page of events...");

                // Get next page using the nextLink
                var pageRequestInfo = new RequestInformation
                {
                    HttpMethod = Method.GET,
                    URI = new Uri(currentResponse.OdataNextLink)
                };
                pageRequestInfo.Headers.Add("Accept", "application/json");

                currentResponse = await _graphClient.RequestAdapter.SendAsync(pageRequestInfo, Microsoft.Graph.Me.Calendars.Item.Events.Delta.DeltaGetResponse.CreateFromDiscriminatorValue);

                var pageEvents = currentResponse?.Value ?? new List<Microsoft.Graph.Models.Event>();
                Console.WriteLine($"   📋 Processing {pageEvents.Count} events from page...");

                foreach (var outlookEvent in pageEvents)
                {
                    await SynchronizeEventAsync(dbCalendar.Id, outlookEvent);
                }
            }

            // Step 3: Extract and save the delta token for future incremental syncs
            var deltaToken = ExtractDeltaTokenFromInitResponse(currentResponse);
            if (!string.IsNullOrEmpty(deltaToken))
            {
                await _outlookChangeProcessor.UpdateCalendarSyncTokenAsync($"{outlookCalendarId}", deltaToken);
                Console.WriteLine($"   🎯 Delta token saved for future incremental syncs");
                Console.WriteLine($"   📄 Token: {deltaToken.Substring(0, Math.Min(80, deltaToken.Length))}...");
            }
            else
            {
                Console.WriteLine($"   ⚠️ Warning: No delta token received - will retry on next sync");
            }

            Console.WriteLine($"   ✅ Full synchronization completed for calendar: {outlookCalendarId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error during full sync for calendar {outlookCalendarId}: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> DeltaSynchronizeCalendarAsync(AccountCalendar calendar)
    {
        var outlookCalendarId = calendar.RemoteCalendarId;

        try
        {
            Console.WriteLine($"   🔍 Starting delta sync for calendar: {outlookCalendarId}");

            var dbCalendarId = $"{outlookCalendarId}";
            var deltaToken = await _outlookChangeProcessor.GetCalendarSyncTokenAsync(dbCalendarId);

            // Check if we have a valid delta token
            if (string.IsNullOrEmpty(deltaToken))
            {
                Console.WriteLine($"   ❌ No delta token found. Please run Initialize Sync Tokens first (Option 18).");
                return false;
            }

            Console.WriteLine($"   ✅ Using stored delta token for incremental sync");

            // Get the database calendar
            var dbCalendar = await _outlookChangeProcessor.GetCalendarByRemoteIdAsync(dbCalendarId);
            if (dbCalendar == null)
            {
                Console.WriteLine($"   ❌ Calendar not found in database: {dbCalendarId}");
                return false;
            }

            try
            {
                // Execute delta query using the stored delta URL
                var eventsResponse = await ExecuteDeltaQueryAsync(deltaToken);

                if (eventsResponse?.Value != null && eventsResponse.Value.Count > 0)
                {
                    Console.WriteLine($"   📥 Processing {eventsResponse.Value.Count} delta changes...");

                    // Process each changed event
                    foreach (var outlookEvent in eventsResponse.Value)
                    {
                        await ProcessOutlookDeltaEventAsync(dbCalendar.Id, outlookEvent, outlookCalendarId);
                    }

                    // Process any additional pages
                    await ProcessDeltaEventsPaginationAsync(dbCalendar.Id, eventsResponse, outlookCalendarId);
                }
                else
                {
                    Console.WriteLine($"   📭 No changes found for calendar");
                }

                // Extract and store the new delta token from @odata.deltaLink
                var newDeltaToken = ExtractDeltaTokenFromResponse(eventsResponse);
                if (!string.IsNullOrEmpty(newDeltaToken))
                {
                    await _outlookChangeProcessor.UpdateCalendarSyncTokenAsync(dbCalendarId, newDeltaToken);
                    Console.WriteLine($"   🔄 Updated delta token for future syncs");
                }
                else
                {
                    Console.WriteLine($"   ⚠️ Warning: No new delta token received");
                }

                return true;
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError odataError) when (odataError.ResponseStatusCode == 410)
            {
                // Delta token expired (HTTP 410 Gone) - recommend full sync
                Console.WriteLine($"   ⚠️ Delta token expired. Run Initialize Sync Tokens (Option 18) to reinitialize.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ Error during delta sync: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Processes a single Outlook event change from delta synchronization
    /// </summary>
    /// <param name="calendarId">The database calendar ID</param>
    /// <param name="outlookEvent">The Outlook event</param>
    private async Task ProcessOutlookDeltaEventAsync(Guid calendarId, Microsoft.Graph.Models.Event outlookEvent, string? outlookCalendarId = null)
    {
        try
        {
            if (string.IsNullOrEmpty(outlookEvent.Id))
            {
                return;
            }

            // Check if this is a deleted event using various Microsoft Graph deletion indicators
            bool isDeleted = false;
            string deletionReason = "";

            // Method 1: Check for @removed annotation in additional data (Microsoft Graph way of indicating deleted items)
            if (outlookEvent.AdditionalData?.ContainsKey("@removed") == true)
            {
                isDeleted = true;
                deletionReason = "Microsoft Graph @removed annotation";
                var removedInfo = outlookEvent.AdditionalData["@removed"];
                Console.WriteLine($"🗑️ Detected deleted event via @removed annotation: {outlookEvent.Id}");
                Console.WriteLine($"   📋 Removal info: {removedInfo}");
            }
            // Method 2: Check for removal reason in additional data
            else if (outlookEvent.AdditionalData?.ContainsKey("reason") == true)
            {
                var reason = outlookEvent.AdditionalData["reason"]?.ToString();
                if (reason == "deleted")
                {
                    isDeleted = true;
                    deletionReason = "Microsoft Graph reason=deleted";
                    Console.WriteLine($"🗑️ Detected deleted event via reason field: {outlookEvent.Id}");
                }
            }
            // Method 3: Check for @odata.context indicating a deleted item
            else if (outlookEvent.AdditionalData?.ContainsKey("@odata.context") == true)
            {
                var context = outlookEvent.AdditionalData["@odata.context"]?.ToString();
                if (context?.Contains("$entity") == true || context?.Contains("deleted") == true)
                {
                    isDeleted = true;
                    deletionReason = "Microsoft Graph @odata.context indicates deletion";
                    Console.WriteLine($"🗑️ Detected deleted event via @odata.context: {outlookEvent.Id}");
                }
            }
            // Method 4: Check if the event is marked as cancelled
            else if (outlookEvent.IsCancelled == true)
            {
                isDeleted = true;
                deletionReason = "Event marked as cancelled";
                Console.WriteLine($"🗑️ Detected cancelled event: {outlookEvent.Subject ?? outlookEvent.Id}");
            }
            // Method 5: Check if all important properties are null/empty (indicating a minimal deleted event response)
            else if (string.IsNullOrEmpty(outlookEvent.Subject) &&
                     outlookEvent.Start == null &&
                     outlookEvent.End == null &&
                     outlookEvent.Organizer == null &&
                     outlookEvent.Body?.Content == null)
            {
                // This might be a deleted event with minimal data - but be cautious
                Console.WriteLine($"🔍 Possible deleted event (minimal data): {outlookEvent.Id}");
                Console.WriteLine($"   📋 Event has only ID, no other properties - investigating...");

                // Try to fetch the event directly to confirm if it's deleted
                try
                {
                    // Get the Outlook calendar ID if not provided
                    if (string.IsNullOrEmpty(outlookCalendarId))
                    {
                        var allCalendars = await _outlookChangeProcessor.GetAllCalendarsAsync();
                        var dbCalendar2 = allCalendars.FirstOrDefault(c => c.Id == calendarId);
                        outlookCalendarId = dbCalendar2?.RemoteCalendarId.Replace("", "");
                    }

                    if (!string.IsNullOrEmpty(outlookCalendarId))
                    {
                        await _graphClient.Me.Calendars[outlookCalendarId].Events[outlookEvent.Id].GetAsync();
                        Console.WriteLine($"   ✅ Event exists, not deleted - will process normally");
                    }
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (ex.ResponseStatusCode == 404)
                {
                    // 404 confirms it's deleted
                    isDeleted = true;
                    deletionReason = "404 Not Found when fetching event details";
                    Console.WriteLine($"🗑️ Confirmed deleted event (404 when fetching): {outlookEvent.Id}");
                }
                catch (Exception)
                {
                    // Other errors - treat as non-deleted but log
                    Console.WriteLine($"   ⚠️ Could not verify deletion status, will process as normal event");
                }
            }

            if (isDeleted)
            {
                // Handle deleted/canceled events
                var eventId = $"{outlookEvent.Id}";
                await _outlookChangeProcessor.MarkEventAsDeletedAsync(eventId, $"{calendarId}");
                Console.WriteLine($"🗑️ Marked Outlook event as deleted: {outlookEvent.Subject ?? outlookEvent.Id}");
                Console.WriteLine($"   📋 Deletion reason: {deletionReason}");
                return;
            }

            // For active events, fetch full event details from API to ensure we have all properties
            try
            {
                // Get the Outlook calendar ID if not provided
                if (string.IsNullOrEmpty(outlookCalendarId))
                {
                    var allCalendars = await _outlookChangeProcessor.GetAllCalendarsAsync();
                    var dbCalendar = allCalendars.FirstOrDefault(c => c.Id == calendarId);

                    if (dbCalendar == null)
                    {
                        Console.WriteLine($"❌ Database calendar not found for ID: {calendarId}");
                        return;
                    }

                    outlookCalendarId = dbCalendar.RemoteCalendarId.Replace("", "");
                }

                // Fetch the complete event with all properties
                var fullEvent = await _graphClient.Me.Calendars[outlookCalendarId].Events[outlookEvent.Id].GetAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Select = new string[] {
                            "id", "subject", "start", "end", "location", "body", "attendees",
                            "organizer", "recurrence", "isAllDay", "isCancelled",
                            "createdDateTime", "lastModifiedDateTime"
                        };
                });

                if (fullEvent != null)
                {
                    // Process the full event data
                    await SynchronizeEventAsync(calendarId, fullEvent);

                    var existingEvent = await _outlookChangeProcessor.GetEventByRemoteIdAsync($"{fullEvent.Id}");
                    var action = existingEvent != null ? "Updated" : "Created";
                    Console.WriteLine($"✅ {action} Outlook event: {fullEvent.Subject ?? "No Subject"} ({fullEvent.Id})");
                }
                else
                {
                    Console.WriteLine($"⚠️ Could not fetch full event details for {outlookEvent.Id}");
                    // Fallback to processing the delta event as-is
                    await SynchronizeEventAsync(calendarId, outlookEvent);
                    var existingEvent = await _outlookChangeProcessor.GetEventByRemoteIdAsync($"{outlookEvent.Id}");
                    var action = existingEvent != null ? "Updated" : "Created";
                    Console.WriteLine($"✅ {action} Outlook event (partial): {outlookEvent.Subject ?? "No Subject"} ({outlookEvent.Id})");
                }
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError odataError) when (odataError.ResponseStatusCode == 404)
            {
                // If we get a 404 when trying to fetch the event, it means it was deleted
                Console.WriteLine($"🗑️ Event {outlookEvent.Id} was deleted (404 Not Found)");
                var eventId = $"{outlookEvent.Id}";
                await _outlookChangeProcessor.MarkEventAsDeletedAsync(eventId, $"{calendarId}");
                Console.WriteLine($"🗑️ Marked Outlook event as deleted: {outlookEvent.Subject ?? outlookEvent.Id}");
            }
            catch (Exception fetchEx)
            {
                Console.WriteLine($"⚠️ Failed to fetch full event details for {outlookEvent.Id}: {fetchEx.Message}");
                // Fallback to processing the delta event as-is
                await SynchronizeEventAsync(calendarId, outlookEvent);
                var existingEvent = await _outlookChangeProcessor.GetEventByRemoteIdAsync($"{outlookEvent.Id}");
                var action = existingEvent != null ? "Updated" : "Created";
                Console.WriteLine($"✅ {action} Outlook event (fallback): {outlookEvent.Subject ?? "No Subject"} ({outlookEvent.Id})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to process Outlook delta event {outlookEvent.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronizes a single Outlook event
    /// </summary>
    /// <param name="calendarId">The database calendar ID</param>
    /// <param name="outlookEvent">The Outlook event to synchronize</param>
    private async Task SynchronizeEventAsync(Guid calendarId, Microsoft.Graph.Models.Event outlookEvent)
    {
        try
        {
            if (string.IsNullOrEmpty(outlookEvent.Id))
            {
                return;
            }

            // Check if event already exists
            var existingEvent = await _outlookChangeProcessor.GetEventByRemoteIdAsync($"{outlookEvent.Id}");

            var eventData = new CalendarItem
            {
                CalendarId = calendarId,
                RemoteEventId = outlookEvent.Id,
                Title = outlookEvent.Subject ?? "No Subject",
                Description = outlookEvent.Body?.Content ?? "",
                Location = outlookEvent.Location?.DisplayName ?? "",
                StartDateTime = ParseEventDateTime(outlookEvent.Start),
                EndDateTime = ParseEventDateTime(outlookEvent.End),
                IsAllDay = outlookEvent.IsAllDay ?? false,
                OrganizerDisplayName = outlookEvent.Organizer?.EmailAddress?.Name,
                OrganizerEmail = outlookEvent.Organizer?.EmailAddress?.Address,
                RecurrenceRules = FormatRecurrence(outlookEvent.Recurrence),
                Status = outlookEvent.IsCancelled == true ? "cancelled" : "confirmed",
                IsDeleted = outlookEvent.IsCancelled == true,
                LastModified = DateTime.UtcNow
            };

            // Automatically determine the calendar item type based on event properties
            eventData.DetermineItemType();

            if (existingEvent != null)
            {
                // Update existing event
                eventData.Id = existingEvent.Id;
                eventData.CreatedDate = existingEvent.CreatedDate;
                await _outlookChangeProcessor.UpdateEventAsync(eventData);
            }
            else
            {
                // Create new event
                eventData.Id = Guid.NewGuid();
                eventData.CreatedDate = DateTime.UtcNow;
                await _outlookChangeProcessor.InsertEventAsync(eventData);
            }

            // Synchronize attendees for this event
            Console.WriteLine($"Synchronizing attendees for event: {outlookEvent.Subject}");
            await SynchronizeEventAttendeesAsync(eventData.Id, outlookEvent.Attendees);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error synchronizing event {outlookEvent.Subject}: {ex.Message}");
            // Continue with other events
        }
    }

    /// <summary>
    /// Formats Outlook recurrence information with enhanced BYDAY and BYMONTHDAY support
    /// </summary>
    /// <param name="recurrence">Outlook recurrence pattern</param>
    /// <returns>Formatted recurrence string in RRULE format</returns>
    private string FormatRecurrence(Microsoft.Graph.Models.PatternedRecurrence? recurrence)
    {
        if (recurrence?.Pattern == null)
        {
            return "";
        }

        var pattern = recurrence.Pattern;
        var parts = new List<string>();

        // Basic frequency mapping
        var freq = pattern.Type switch
        {
            Microsoft.Graph.Models.RecurrencePatternType.Daily => "DAILY",
            Microsoft.Graph.Models.RecurrencePatternType.Weekly => "WEEKLY",
            Microsoft.Graph.Models.RecurrencePatternType.AbsoluteMonthly => "MONTHLY",
            Microsoft.Graph.Models.RecurrencePatternType.RelativeMonthly => "MONTHLY",
            Microsoft.Graph.Models.RecurrencePatternType.AbsoluteYearly => "YEARLY",
            Microsoft.Graph.Models.RecurrencePatternType.RelativeYearly => "YEARLY",
            _ => "DAILY"
        };
        parts.Add($"FREQ={freq}");

        // Interval
        if (pattern.Interval > 1)
        {
            parts.Add($"INTERVAL={pattern.Interval}");
        }

        // Handle BYDAY for weekly and monthly patterns
        if (pattern.DaysOfWeek != null && pattern.DaysOfWeek.Any())
        {
            var byDayValues = new List<string>();

            foreach (var dayOfWeekObj in pattern.DaysOfWeek)
            {
                // Convert DayOfWeekObject to string representation
                string? dayCode = null;
                try
                {
                    // Use ToString() to get the day of week representation
                    var dayString = dayOfWeekObj?.ToString()?.ToLowerInvariant();
                    dayCode = dayString switch
                    {
                        "sunday" => "SU",
                        "monday" => "MO",
                        "tuesday" => "TU",
                        "wednesday" => "WE",
                        "thursday" => "TH",
                        "friday" => "FR",
                        "saturday" => "SA",
                        _ => null
                    };
                }
                catch
                {
                    // If conversion fails, skip this day
                    continue;
                }

                if (dayCode != null)
                {
                    // For relative monthly patterns (e.g., first Monday, last Friday)
                    if (pattern.Type == Microsoft.Graph.Models.RecurrencePatternType.RelativeMonthly && pattern.Index != null)
                    {
                        var indexCode = pattern.Index switch
                        {
                            Microsoft.Graph.Models.WeekIndex.First => "1",
                            Microsoft.Graph.Models.WeekIndex.Second => "2",
                            Microsoft.Graph.Models.WeekIndex.Third => "3",
                            Microsoft.Graph.Models.WeekIndex.Fourth => "4",
                            Microsoft.Graph.Models.WeekIndex.Last => "-1",
                            _ => ""
                        };
                        if (!string.IsNullOrEmpty(indexCode))
                        {
                            byDayValues.Add($"{indexCode}{dayCode}");
                        }
                    }
                    else
                    {
                        byDayValues.Add(dayCode);
                    }
                }
            }

            if (byDayValues.Any())
            {
                parts.Add($"BYDAY={string.Join(",", byDayValues)}");
            }
        }

        // Handle BYMONTHDAY for absolute monthly patterns
        if (pattern.Type == Microsoft.Graph.Models.RecurrencePatternType.AbsoluteMonthly && pattern.DayOfMonth > 0)
        {
            parts.Add($"BYMONTHDAY={pattern.DayOfMonth}");
        }

        // Handle BYMONTH for yearly patterns
        if ((pattern.Type == Microsoft.Graph.Models.RecurrencePatternType.AbsoluteYearly ||
             pattern.Type == Microsoft.Graph.Models.RecurrencePatternType.RelativeYearly) &&
            pattern.Month > 0)
        {
            parts.Add($"BYMONTH={pattern.Month}");
        }

        // Handle COUNT and UNTIL from recurrence range
        if (recurrence.Range != null)
        {
            switch (recurrence.Range.Type)
            {
                case Microsoft.Graph.Models.RecurrenceRangeType.Numbered:
                    if (recurrence.Range.NumberOfOccurrences > 0)
                    {
                        parts.Add($"COUNT={recurrence.Range.NumberOfOccurrences}");
                    }
                    break;

                case Microsoft.Graph.Models.RecurrenceRangeType.EndDate:
                    if (recurrence.Range.EndDate != null)
                    {
                        // Convert Microsoft.Kiota.Abstractions.Date to DateTime
                        try
                        {
                            var endDateString = recurrence.Range.EndDate.ToString();
                            if (DateTime.TryParse(endDateString, out var endDate))
                            {
                                // Convert to RRULE UNTIL format (YYYYMMDDTHHMMSSZ)
                                var utcEndDate = endDate.ToUniversalTime();
                                parts.Add($"UNTIL={utcEndDate:yyyyMMddTHHmmss}Z");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: Could not parse end date for recurrence: {ex.Message}");
                        }
                    }
                    break;
            }
        }

        // Return empty string if no parts were added
        if (parts.Count == 0)
        {
            return "";
        }

        // Join the parts and add the RRULE: prefix for compatibility with ExpandRecurringEvent
        return $"RRULE:{string.Join(";", parts)}";
    }

    /// <summary>
    /// Parses Outlook event date/time
    /// </summary>
    /// <param name="dateTime">Outlook DateTimeTimeZone</param>
    /// <returns>Parsed DateTime</returns>
    private DateTime ParseEventDateTime(Microsoft.Graph.Models.DateTimeTimeZone? dateTime)
    {
        if (dateTime?.DateTime == null)
        {
            return DateTime.UtcNow;
        }

        if (DateTime.TryParse(dateTime.DateTime, out var parsed))
        {
            // Convert to UTC if timezone info is available
            if (!string.IsNullOrEmpty(dateTime.TimeZone) && dateTime.TimeZone != "UTC")
            {
                try
                {
                    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(dateTime.TimeZone);
                    return TimeZoneInfo.ConvertTimeToUtc(parsed, timeZone);
                }
                catch
                {
                    // If timezone conversion fails, assume the time is already in the correct zone
                    return parsed;
                }
            }
            return parsed;
        }

        return DateTime.UtcNow;
    }

    /// <summary>
    /// Synchronizes attendees for an event
    /// </summary>
    /// <param name="eventId">The database event ID</param>
    /// <param name="outlookAttendees">The Outlook attendees</param>
    private async Task SynchronizeEventAttendeesAsync(Guid eventId, IList<Microsoft.Graph.Models.Attendee>? outlookAttendees)
    {
        try
        {
            // Clear existing attendees for this event
            await _outlookChangeProcessor.DeleteCalendarEventAttendeesForEventAsync(eventId);

            if (outlookAttendees == null || !outlookAttendees.Any())
            {
                Console.WriteLine($"No attendees found for event {eventId}");
                return;
            }

            Console.WriteLine($"Synchronizing {outlookAttendees.Count} attendees for event {eventId}");
            var attendees = new List<CalendarEventAttendee>();

            foreach (var outlookAttendee in outlookAttendees)
            {
                if (outlookAttendee.EmailAddress?.Address == null)
                {
                    Console.WriteLine($"Skipping attendee with no email address");
                    continue;
                }

                var attendee = new CalendarEventAttendee
                {
                    Id = Guid.NewGuid(),
                    EventId = eventId,
                    Email = outlookAttendee.EmailAddress.Address,
                    DisplayName = outlookAttendee.EmailAddress.Name,
                    ResponseStatus = OutlookIntegratorExtensions.ConvertOutlookResponseStatus(outlookAttendee.Status?.Response),
                    IsOptional = outlookAttendee.Type == Microsoft.Graph.Models.AttendeeType.Optional,
                    IsOrganizer = outlookAttendee.Status?.Response == Microsoft.Graph.Models.ResponseType.Organizer,
                    IsSelf = false, // Outlook doesn't provide this directly
                    Comment = "", // Outlook doesn't provide attendee comments
                    AdditionalGuests = 0, // Outlook doesn't provide this
                    CreatedDate = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow
                };

                Console.WriteLine($"Adding attendee: {attendee.Email} ({attendee.ResponseStatus})");
                attendees.Add(attendee);
            }

            // Add all attendees
            foreach (var attendee in attendees)
            {
                await _outlookChangeProcessor.InsertCalendarEventAttendeeAsync(attendee);
            }

            Console.WriteLine($"Successfully synchronized {attendees.Count} attendees");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error synchronizing attendees for event: {ex.Message}");
        }
    }

    private async Task SynchronizeCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var calendars = await _graphClient.Me.Calendars.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var localCalendars = await _outlookChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        List<AccountCalendar> insertedCalendars = new();
        List<AccountCalendar> updatedCalendars = new();
        List<AccountCalendar> deletedCalendars = new();

        // 1. Handle deleted calendars.

        foreach (var calendar in localCalendars)
        {
            var remoteCalendar = calendars.Value.FirstOrDefault(a => a.Id == calendar.RemoteCalendarId);
            if (remoteCalendar == null)
            {
                // Local calendar doesn't exists remotely. Delete local copy.

                await _outlookChangeProcessor.DeleteAccountCalendarAsync(calendar).ConfigureAwait(false);
                deletedCalendars.Add(calendar);
            }
        }

        // Delete the deleted folders from local list.
        deletedCalendars.ForEach(a => localCalendars.Remove(a));

        // 2. Handle update/insert based on remote calendars.
        foreach (var calendar in calendars.Value)
        {
            var existingLocalCalendar = localCalendars.FirstOrDefault(a => a.RemoteCalendarId == calendar.Id);
            if (existingLocalCalendar == null)
            {
                // Insert new calendar.
                var localCalendar = calendar.AsCalendar(Account);
                insertedCalendars.Add(localCalendar);
            }
            else
            {
                // Update existing calendar. Right now we only update the name.
                if (ShouldUpdateCalendar(calendar, existingLocalCalendar))
                {
                    existingLocalCalendar.Name = calendar.Name;

                    updatedCalendars.Add(existingLocalCalendar);
                }
                else
                {
                    // Remove it from the local folder list to skip additional calendar updates.
                    localCalendars.Remove(existingLocalCalendar);
                }
            }
        }

        // 3.Process changes in order-> Insert, Update. Deleted ones are already processed.
        foreach (var calendar in insertedCalendars)
        {
            await _outlookChangeProcessor.InsertAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        foreach (var calendar in updatedCalendars)
        {
            await _outlookChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        if (insertedCalendars.Any() || deletedCalendars.Any() || updatedCalendars.Any())
        {
            // TODO: Notify calendar updates.
            // WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
        }
    }

    private bool ShouldUpdateCalendar(Calendar calendar, AccountCalendar accountCalendar)
    {
        // TODO: Only calendar name is updated for now. We can add more checks here.

        var remoteCalendarName = calendar.Name;
        var localCalendarName = accountCalendar.Name;

        return !localCalendarName.Equals(remoteCalendarName, StringComparison.OrdinalIgnoreCase);
    }

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync();

        _graphClient.Dispose();
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Graph;
using Microsoft.Graph.Me.MailFolders.Item.Messages.Delta;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using MimeKit;
using MoreLinq.Extensions;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Http;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.Misc;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Category;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers.Mail;

[JsonSerializable(typeof(Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody))]
public partial class OutlookSynchronizerJsonContext : JsonSerializerContext;

/// <summary>
/// Outlook synchronizer implementation with delta token synchronization.
/// 
/// SYNCHRONIZATION STRATEGY:
/// - Uses delta API for both initial and incremental sync
/// - Initial sync: Downloads messages using the account's configured cutoff date with metadata only
/// - Incremental sync: Uses delta token to get only changes since last sync
/// - Messages are downloaded with metadata only (no MIME content during sync)
/// - MIME files are downloaded on-demand when user explicitly reads a message
/// 
/// Key implementation details:
/// - SynchronizeFolderAsync: Main entry point for per-folder synchronization
/// - DownloadMailsForInitialSyncAsync: Downloads messages using delta API with an optional cutoff filter
/// - ProcessDeltaChangesAsync: Processes incremental changes using delta token
/// - DownloadMessageMetadataBatchAsync: Downloads metadata in batches using Graph batch API
/// - CreateMailCopyFromMessageAsync: Creates MailCopy from Message metadata
/// - DownloadMissingMimeMessageAsync: Downloads raw MIME only when explicitly requested
/// </summary>
public class OutlookSynchronizer : WinoSynchronizer<RequestInformation, Message, Event>
{
    public override uint BatchModificationSize => 20;
    public override uint InitialMessageDownloadCountPerFolder => 1000;
    private const uint MaximumAllowedBatchRequestSize = 20;
    private const int SimpleAttachmentUploadLimitBytes = 3 * 1024 * 1024;
    private const int MaximumUploadSessionAttachmentSizeBytes = 150 * 1024 * 1024;
    private const int LargeAttachmentUploadChunkSizeBytes = 320 * 1024;

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
        "IsReadReceiptRequested",
        "IsDraft",
        "ReceivedDateTime",
        "HasAttachments",
        "BodyPreview",
        "Id",
        "ConversationId",
        "From",
        "Sender",
        "ToRecipients",
        "CcRecipients",
        "BccRecipients",
        "ReplyTo",
        "Subject",
        "ParentFolderId",
        "InternetMessageId",
        "InternetMessageHeaders",
        "Categories",
    ];

    private readonly SemaphoreSlim _handleItemRetrievalSemaphore = new(1);
    private readonly SemaphoreSlim _handleCalendarEventRetrievalSemaphore = new(1);

    private readonly ILogger _logger = Log.ForContext<OutlookSynchronizer>();
    private readonly IOutlookChangeProcessor _outlookChangeProcessor;
    private readonly GraphServiceClient _graphClient;
    private readonly IOutlookSynchronizerErrorHandlerFactory _errorHandlingFactory;
    private readonly IMailCategoryService _mailCategoryService;
    private bool _isFolderStructureChanged;

    private readonly SemaphoreSlim _concurrentDownloadSemaphore = new(10); // Limit to 10 concurrent downloads

    public OutlookSynchronizer(MailAccount account,
                               IAuthenticator authenticator,
                               IOutlookChangeProcessor outlookChangeProcessor,
                               IOutlookSynchronizerErrorHandlerFactory errorHandlingFactory,
                               IMailCategoryService mailCategoryService) : base(account, WeakReferenceMessenger.Default)
    {
        var tokenProvider = new MicrosoftTokenProvider(Account, authenticator);

        // Update request handlers for Graph client.
        var handlers = GraphClientFactory.CreateDefaultHandlers();

        handlers.Add(GetMicrosoftImmutableIdHandler());
        handlers.Add(GetGraphRateLimitHandler());

        var httpClient = GraphClientFactory.Create(handlers);
        _graphClient = new GraphServiceClient(httpClient, new BaseBearerTokenAuthenticationProvider(tokenProvider));

        _outlookChangeProcessor = outlookChangeProcessor;
        _errorHandlingFactory = errorHandlingFactory;
        _mailCategoryService = mailCategoryService;
    }

    #region MS Graph Handlers

    private MicrosoftImmutableIdHandler GetMicrosoftImmutableIdHandler() => new();

    private GraphRateLimitHandler GetGraphRateLimitHandler() => new();

    #endregion


    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();
        var folderResults = new List<FolderSyncResult>();

        _logger.Information("Internal synchronization started for {Name}", Account.Name);
        _logger.Information("Options: {Options}", options);

        try
        {
            // Set indeterminate progress initially
            UpdateSyncProgress(0, 0, "Synchronizing folders...");

            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);

            if (options.Type != MailSynchronizationType.FoldersOnly)
            {
                var synchronizationFolders = await _outlookChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);

                _logger.Information(string.Format("{1} Folders: {0}", string.Join(",", synchronizationFolders.Select(a => a.FolderName)), synchronizationFolders.Count));

                var totalFolders = synchronizationFolders.Count;

                for (int i = 0; i < totalFolders; i++)
                {
                    var folder = synchronizationFolders[i];

                    // Update progress based on folder completion
                    var progressPercentage = (int)Math.Round((double)(i + 1) / totalFolders * 100);
                    var statusMessage = string.Format(Translator.Sync_SynchronizingFolder, folder.FolderName, progressPercentage);
                    UpdateSyncProgress(totalFolders, totalFolders - (i + 1), statusMessage);

                    try
                    {
                        var folderDownloadedMessageIds = await SynchronizeFolderAsync(folder, cancellationToken).ConfigureAwait(false);
                        downloadedMessageIds.AddRange(folderDownloadedMessageIds);

                        folderResults.Add(FolderSyncResult.Successful(folder.Id, folder.FolderName, folderDownloadedMessageIds.Count()));
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation should stop the entire sync
                        throw;
                    }
                    catch (ODataError odataError)
                    {
                        // Handle OData errors - determine if we should continue or stop
                        var errorContext = new SynchronizerErrorContext
                        {
                            Account = Account,
                            ErrorCode = (int?)odataError.ResponseStatusCode,
                            ErrorMessage = odataError.Error?.Message ?? odataError.Message,
                            Exception = odataError,
                            FolderId = folder.Id,
                            FolderName = folder.FolderName,
                            OperationType = "FolderSync"
                        };

                        var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

                        if (errorContext.CanContinueSync)
                        {
                            _logger.Warning("Folder {FolderName} sync failed with recoverable error, continuing with other folders. Error: {Error}",
                                folder.FolderName, odataError.Error?.Message);
                            folderResults.Add(FolderSyncResult.Failed(folder.Id, folder.FolderName, errorContext));
                        }
                        else
                        {
                            _logger.Error(odataError, "Folder {FolderName} sync failed with fatal error, stopping sync", folder.FolderName);
                            folderResults.Add(FolderSyncResult.Failed(folder.Id, folder.FolderName, errorContext));
                            throw;
                        }
                    }
                    catch (Exception ex)
                    {
                        // For unexpected exceptions, try to classify and decide if we should continue
                        var errorContext = new SynchronizerErrorContext
                        {
                            Account = Account,
                            ErrorMessage = ex.Message,
                            Exception = ex,
                            FolderId = folder.Id,
                            FolderName = folder.FolderName,
                            OperationType = "FolderSync",
                            Severity = SynchronizerErrorSeverity.Recoverable, // Default to recoverable for individual folders
                            Category = SynchronizerErrorCategory.Unknown
                        };

                        _logger.Warning(ex, "Folder {FolderName} sync failed, continuing with other folders", folder.FolderName);
                        folderResults.Add(FolderSyncResult.Failed(folder.Id, folder.FolderName, errorContext));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Synchronization was canceled for {Name}", Account.Name);
            return MailSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Synchronizing folders for {Name}", Account.Name);
            return MailSynchronizationResult.Failed(ex);
        }
        finally
        {
            // Reset progress at the end
            ResetSyncProgress();
        }

        // Get all unread new downloaded items and return in the result.
        // This is primarily used in notifications.

        var unreadNewItems = await _outlookChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);

        return MailSynchronizationResult.CompletedWithFolderResults(unreadNewItems, folderResults);
    }

    public Task DownloadSearchResultMessageAsync(string messageId, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
        => DownloadSearchResultMessageAsync(messageId, assignedFolder, existingMessageIds: null, cancellationToken);

    private async Task DownloadSearchResultMessageAsync(string messageId,
                                                        MailItemFolder assignedFolder,
                                                        ISet<string> existingMessageIds,
                                                        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId) || assignedFolder == null) return;

        // Online search can return the same message across repeated invocations/races.
        // Guard before network+MIME download and before database insert.
        if (existingMessageIds?.Contains(messageId) == true)
        {
            return;
        }

        if (existingMessageIds == null)
        {
            var existing = await _outlookChangeProcessor.AreMailsExistsAsync([messageId]).ConfigureAwait(false);
            if (existing.Contains(messageId))
            {
                return;
            }
        }

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

        // Check if this is an EventMessage and fetch it separately if needed (only if calendar access granted)
        if (Account.IsCalendarAccessGranted && message is EventMessage)
        {
            message = await FetchEventMessageAsync(message.Id, cancellationToken).ConfigureAwait(false);
            if (message == null)
            {
                _logger.Warning("Failed to fetch EventMessage {MessageId}, skipping", messageId);
                return;
            }
        }

        var mailPackages = await CreateNewMailPackagesAsync(message, assignedFolder, cancellationToken).ConfigureAwait(false);

        if (mailPackages == null) return;

        foreach (var package in mailPackages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Use safe upsert path to avoid duplicate rows when message already exists.
            await _outlookChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);
        }

        existingMessageIds?.Add(messageId);
    }

    private async Task<IEnumerable<string>> SynchronizeFolderAsync(MailItemFolder folder, CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();

        cancellationToken.ThrowIfCancellationRequested();

        _logger.Debug("Synchronizing {FolderName} using delta API", folder.FolderName);

        // Check if we have a delta token
        if (string.IsNullOrEmpty(folder.DeltaToken))
        {
            _logger.Debug("No delta token for folder {FolderName}. Starting initial sync.", folder.FolderName);

            // Download mails for initial sync using the account's configured cutoff date.
            await DownloadMailsForInitialSyncAsync(folder, downloadedMessageIds, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Initial sync is completed, process delta changes
            _logger.Debug("Delta token exists for folder {FolderName}. Processing incremental changes.", folder.FolderName);

            await ProcessDeltaChangesAsync(folder, downloadedMessageIds, cancellationToken).ConfigureAwait(false);
        }

        await _outlookChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

        if (downloadedMessageIds.Any())
        {
            _logger.Information("Downloaded {Count} messages for folder {FolderName}", downloadedMessageIds.Count, folder.FolderName);
        }

        return downloadedMessageIds;
    }

    /// <summary>
    /// Downloads mails for initial synchronization using Delta API with the account's configured cutoff date.
    /// Downloads metadata only (no MIME content) for messages received after that date.
    /// </summary>
    private async Task DownloadMailsForInitialSyncAsync(MailItemFolder folder, List<string> downloadedMessageIds, CancellationToken cancellationToken)
    {
        _logger.Debug("Starting initial mail download for folder {FolderName}", folder.FolderName);

        try
        {
            var referenceDateUtc = Account.CreatedAt ?? DateTime.UtcNow;
            var initialSynchronizationCutoffDateUtc = Account.InitialSynchronizationRange.ToCutoffDateUtc(referenceDateUtc);
            var filterDate = initialSynchronizationCutoffDateUtc?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

            if (filterDate != null)
            {
                _logger.Information("Downloading messages received after {FilterDate} for folder {FolderName}", filterDate, folder.FolderName);
            }
            else
            {
                _logger.Information("Downloading all available messages for folder {FolderName}", folder.FolderName);
            }

            var messageCollectionPage = await _graphClient.Me.MailFolders[folder.RemoteFolderId].Messages.Delta.GetAsDeltaGetResponseAsync((config) =>
            {
                config.QueryParameters.Select = outlookMessageSelectParameters;
                config.QueryParameters.Orderby = ["receivedDateTime desc"];

                if (filterDate != null)
                {
                    config.QueryParameters.Filter = $"receivedDateTime ge {filterDate}";
                }
            }, cancellationToken).ConfigureAwait(false);

            var totalProcessed = 0;

            // Use PageIterator to process all messages
            var messageIterator = PageIterator<Message, DeltaGetResponse>.CreatePageIterator(_graphClient, messageCollectionPage, async (message) =>
            {
                try
                {
                    await _handleItemRetrievalSemaphore.WaitAsync();

                    if (!IsResourceDeleted(message.AdditionalData) && !IsNotRealMessageType(message))
                    {
                        // Check if this is an EventMessage and fetch it separately if needed (only if calendar access granted)
                        if (Account.IsCalendarAccessGranted && message is EventMessage)
                        {
                            message = await FetchEventMessageAsync(message.Id, cancellationToken).ConfigureAwait(false);
                            if (message == null)
                            {
                                return true; // Skip this message if fetch failed
                            }
                        }

                        // Check if message already exists
                        bool mailExists = await _outlookChangeProcessor.IsMailExistsInFolderAsync(message.Id, folder.Id).ConfigureAwait(false);

                        if (!mailExists)
                        {
                            // For drafts and calendar invitations, download MIME during initial sync like delta sync.
                            var itemType = Account.IsCalendarAccessGranted ? message.GetMailItemType() : MailItemType.Mail;
                            if (ShouldDownloadMimeForMessage(message, folder, itemType))
                            {
                                var draftPackages = await CreateNewMailPackagesAsync(message, folder, cancellationToken).ConfigureAwait(false);

                                if (draftPackages != null)
                                {
                                    foreach (var package in draftPackages)
                                    {
                                        bool isInserted = await _outlookChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);
                                        if (isInserted)
                                        {
                                            downloadedMessageIds.Add(package.Copy.Id);
                                            totalProcessed++;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Create MailCopy from metadata
                                var mailCopy = await CreateMailCopyFromMessageAsync(message, folder).ConfigureAwait(false);

                                if (mailCopy != null)
                                {
                                    // Create package without MIME
                                    var contacts = ExtractContactsFromOutlookMessage(message);
                                    var package = new NewMailItemPackage(mailCopy, null, folder.RemoteFolderId, contacts);
                                    bool isInserted = await _outlookChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);

                                    if (isInserted)
                                    {
                                        downloadedMessageIds.Add(mailCopy.Id);
                                        totalProcessed++;
                                    }
                                }
                            }

                            // Update progress periodically
                            if (totalProcessed > 0 && totalProcessed % 50 == 0)
                            {
                                var statusMessage = string.Format(Translator.Sync_DownloadedMessages, totalProcessed, folder.FolderName);
                                UpdateSyncProgress(0, 0, statusMessage);
                            }
                        }
                        else
                        {
                            _logger.Debug("Mail {MailId} already exists in folder {FolderName}, skipping", message.Id, folder.FolderName);
                        }
                    }

                    return true; // Continue processing
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to process message {MessageId} during initial sync for folder {FolderName}", message.Id, folder.FolderName);
                    return true; // Continue despite error
                }
                finally
                {
                    _handleItemRetrievalSemaphore.Release();
                }
            });

            await messageIterator.IterateAsync(cancellationToken).ConfigureAwait(false);

            // Extract and store delta token for future incremental syncs
            if (!string.IsNullOrEmpty(messageIterator.Deltalink))
            {
                var deltaToken = GetDeltaTokenFromDeltaLink(messageIterator.Deltalink);
                await _outlookChangeProcessor.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, deltaToken).ConfigureAwait(false);
                await _outlookChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);
                folder.DeltaToken = deltaToken;
                _logger.Information("Stored delta token for folder {FolderName} - future syncs will be incremental", folder.FolderName);
            }
            else
            {
                _logger.Warning("No delta token received for folder {FolderName} - future syncs may re-download messages", folder.FolderName);
            }

            _logger.Information("Initial sync completed for folder {FolderName}. Downloaded {Count} messages", folder.FolderName, totalProcessed);
        }
        catch (ApiException apiException)
        {
            // Handle API errors
            var errorContext = new SynchronizerErrorContext
            {
                Account = Account,
                ErrorCode = (int?)apiException.ResponseStatusCode,
                ErrorMessage = $"API error during initial sync: {apiException.Message}",
                Exception = apiException
            };

            var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

            if (handled)
            {
                if (apiException.ResponseStatusCode == 410)
                {
                    folder.DeltaToken = string.Empty;
                    _logger.Information("API error handled successfully for folder {FolderName} during initial sync. Error: {ErrorCode}", folder.FolderName, apiException.ResponseStatusCode);
                }
            }
            else
            {
                _logger.Error(apiException, "Unhandled API error during initial sync for folder {FolderName}. Error: {ErrorCode}", folder.FolderName, apiException.ResponseStatusCode);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error occurred during initial mail download for folder {FolderName}", folder.FolderName);
            throw;
        }
    }

    /// <summary>
    /// Downloads metadata for a batch of messages using Graph SDK batch API (no MIME content).
    /// Processes up to 20 messages per batch request as per MaximumAllowedBatchRequestSize.
    /// </summary>
    private async Task<List<string>> DownloadMessageMetadataBatchAsync(List<string> messageIds, MailItemFolder folder, bool retryFailedOnce, CancellationToken cancellationToken)
    {
        if (messageIds == null || messageIds.Count == 0)
            return new List<string>();

        var downloadedIds = new List<string>();

        // Filter out messages that already exist in the database
        var messagesToDownload = new List<string>();
        foreach (var messageId in messageIds)
        {
            bool mailExists = await _outlookChangeProcessor.IsMailExistsInFolderAsync(messageId, folder.Id).ConfigureAwait(false);
            if (!mailExists)
            {
                messagesToDownload.Add(messageId);
            }
            else
            {
                _logger.Debug("Mail {MailId} already exists in folder {FolderName}, skipping download", messageId, folder.FolderName);
            }
        }

        if (messagesToDownload.Count == 0)
        {
            _logger.Debug("All messages already exist in folder {FolderName}", folder.FolderName);
            return downloadedIds;
        }

        // Store failed message ids to retry after.

        List<string> failedMessageIds = new();

        // Process in batches of MaximumAllowedBatchRequestSize (20)
        var batches = messagesToDownload.Batch((int)MaximumAllowedBatchRequestSize);

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var batchContent = new BatchRequestContentCollection(_graphClient);
                var requestIdToMessageIdMap = new Dictionary<string, string>();

                // Add all message requests to the batch
                foreach (var messageId in batch)
                {
                    var requestInfo = _graphClient.Me.Messages[messageId].ToGetRequestInformation((config) =>
                    {
                        config.QueryParameters.Select = outlookMessageSelectParameters;
                    });

                    var batchRequestId = await batchContent.AddBatchRequestStepAsync(requestInfo).ConfigureAwait(false);
                    requestIdToMessageIdMap[batchRequestId] = messageId;
                }

                // Execute the batch request
                var batchResponse = await _graphClient.Batch.PostAsync(batchContent, cancellationToken).ConfigureAwait(false);

                // Process all responses
                foreach (var batchRequestId in requestIdToMessageIdMap.Keys)
                {
                    var messageId = requestIdToMessageIdMap[batchRequestId];

                    try
                    {
                        // Deserialize the Message directly from batch response
                        var message = await batchResponse.GetResponseByIdAsync<Message>(batchRequestId).ConfigureAwait(false);

                        if (message != null)
                        {
                            var itemType = Account.IsCalendarAccessGranted ? message.GetMailItemType() : MailItemType.Mail;

                            if (ShouldDownloadMimeForMessage(message, folder, itemType))
                            {
                                var packages = await CreateNewMailPackagesAsync(message, folder, cancellationToken).ConfigureAwait(false);

                                if (packages != null)
                                {
                                    foreach (var package in packages)
                                    {
                                        bool isInserted = await _outlookChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);

                                        if (isInserted)
                                        {
                                            downloadedIds.Add(package.Copy.Id);
                                            _logger.Debug("Downloaded MIME-backed message {MailId} in folder {FolderName}", messageId, folder.FolderName);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Create MailCopy from metadata only
                                var mailCopy = await CreateMailCopyFromMessageAsync(message, folder).ConfigureAwait(false);

                                if (mailCopy != null)
                                {
                                    // Create package without MIME
                                    var contacts = ExtractContactsFromOutlookMessage(message);
                                    var package = new NewMailItemPackage(mailCopy, null, folder.RemoteFolderId, contacts);
                                    bool isInserted = await _outlookChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);

                                    if (isInserted)
                                    {
                                        downloadedIds.Add(mailCopy.Id);
                                        _logger.Debug("Downloaded metadata for message {MailId} in folder {FolderName}", messageId, folder.FolderName);
                                    }
                                    else
                                    {
                                        _logger.Warning("Failed to insert mail {MailId} for folder {FolderName}", messageId, folder.FolderName);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.Warning("Failed to deserialize message {MailId} for folder {FolderName}", messageId, folder.FolderName);
                            failedMessageIds.Add(messageId);
                        }
                    }
                    catch (ODataError odataError)
                    {
                        // Handle OData errors from the batch response
                        if (odataError.ResponseStatusCode == 404)
                        {
                            _logger.Warning("Mail {MailId} not found on server (404) for folder {FolderName}", messageId, folder.FolderName);
                        }
                        else
                        {
                            failedMessageIds.Add(messageId);
                            _logger.Error("OData error while downloading mail {MailId} for folder {FolderName}. Error: {Error}", messageId, folder.FolderName, odataError.Error?.Message);
                        }
                    }
                    catch (ServiceException serviceException)
                    {
                        // Try to handle the error using the error handling factory
                        var errorContext = new SynchronizerErrorContext
                        {
                            Account = Account,
                            ErrorCode = (int?)serviceException.ResponseStatusCode,
                            ErrorMessage = $"Service error during batch mail download: {serviceException.Message}",
                            Exception = serviceException,
                        };

                        var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

                        if (!handled)
                        {
                            failedMessageIds.Add(messageId);
                            _logger.Error(serviceException, "Unhandled service error while downloading mail {MailId} for folder {FolderName}. Error: {ErrorCode}", messageId, folder.FolderName, serviceException.ResponseStatusCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        failedMessageIds.Add(messageId);
                        _logger.Error(ex, "Error occurred while processing message {MailId} for folder {FolderName}", messageId, folder.FolderName);
                    }
                }
            }
            catch (Exception ex)
            {
                failedMessageIds.AddRange(batch);

                _logger.Error(ex, "Error occurred during batch download for folder {FolderName}", folder.FolderName);
            }
        }

        if (retryFailedOnce && failedMessageIds.Any())
        {
            // For a good cause wait a little bit.

            await Task.Delay(3000);

            // Do not retry here once again.
            var failedDownloadedMessagIds = await DownloadMessageMetadataBatchAsync(failedMessageIds, folder, false, cancellationToken);

            downloadedIds.Concat(failedDownloadedMessagIds);
        }

        return downloadedIds;
    }

    /// <summary>
    /// Creates a MailCopy from an Outlook Message with metadata only (centralized method).
    /// This replaces the scattered CreateMinimalMailCopyAsync and AsMailCopy calls.
    /// </summary>
    private static bool ShouldDownloadMimeForMessage(Message message, MailItemFolder folder, MailItemType itemType)
        => folder.SpecialFolderType == SpecialFolderType.Draft
           || itemType == MailItemType.CalendarInvitation
           || LooksLikeReadReceipt(message);

    private static bool LooksLikeReadReceipt(Message message)
    {
        var contentType = message?.InternetMessageHeaders?
            .FirstOrDefault(h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return !string.IsNullOrWhiteSpace(contentType)
               && contentType.Contains("disposition-notification", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MailCopy> CreateMailCopyFromMessageAsync(Message message, MailItemFolder assignedFolder)
    {
        if (message == null) return null;

        var mailCopy = message.AsMailCopy();
        mailCopy.FolderId = assignedFolder.Id;
        mailCopy.UniqueId = Guid.NewGuid();
        mailCopy.FileId = Guid.NewGuid();

        // Set ItemType based on calendar access permissions
        if (Account.IsCalendarAccessGranted && message is EventMessage)
        {
            mailCopy.ItemType = message.GetMailItemType();
        }

        // Check for draft mapping if this is a draft with WinoLocalDraftHeader
        if (message.IsDraft.GetValueOrDefault() && message.InternetMessageHeaders != null)
        {
            var winoDraftHeader = message.InternetMessageHeaders
                .FirstOrDefault(h => string.Equals(h.Name, Domain.Constants.WinoLocalDraftHeader, StringComparison.OrdinalIgnoreCase));

            if (winoDraftHeader != null && Guid.TryParse(winoDraftHeader.Value, out Guid localDraftCopyUniqueId))
            {
                // This message belongs to existing local draft copy.
                // We don't need to create a new mail copy for this message, just update the existing one.

                bool isMappingSuccessful = await _outlookChangeProcessor.MapLocalDraftAsync(
                    Account.Id,
                    localDraftCopyUniqueId,
                    mailCopy.Id,
                    mailCopy.DraftId,
                    mailCopy.ThreadId);

                if (isMappingSuccessful)
                {
                    _logger.Debug("Successfully mapped remote draft {RemoteId} to local draft {LocalId}",
                        mailCopy.Id, localDraftCopyUniqueId);
                    return null; // Don't create new mail copy, existing one was updated
                }

                // Local copy doesn't exist. Continue execution to insert mail copy.
                _logger.Debug("Local draft copy {LocalId} not found, creating new mail copy for {RemoteId}",
                    localDraftCopyUniqueId, mailCopy.Id);
            }
        }

        return mailCopy;
    }

    private static IReadOnlyList<AccountContact> ExtractContactsFromOutlookMessage(Message message)
    {
        if (message == null) return [];

        var contacts = new Dictionary<string, AccountContact>(StringComparer.OrdinalIgnoreCase);

        AddRecipient(message.From?.EmailAddress);
        AddRecipient(message.Sender?.EmailAddress);

        if (message.ToRecipients != null)
        {
            foreach (var recipient in message.ToRecipients)
            {
                AddRecipient(recipient?.EmailAddress);
            }
        }

        if (message.CcRecipients != null)
        {
            foreach (var recipient in message.CcRecipients)
            {
                AddRecipient(recipient?.EmailAddress);
            }
        }

        if (message.BccRecipients != null)
        {
            foreach (var recipient in message.BccRecipients)
            {
                AddRecipient(recipient?.EmailAddress);
            }
        }

        if (message.ReplyTo != null)
        {
            foreach (var recipient in message.ReplyTo)
            {
                AddRecipient(recipient?.EmailAddress);
            }
        }

        return contacts.Values.ToList();

        void AddRecipient(EmailAddress emailAddress)
        {
            var address = emailAddress?.Address?.Trim();
            if (string.IsNullOrWhiteSpace(address)) return;

            var displayName = string.IsNullOrWhiteSpace(emailAddress.Name) ? address : emailAddress.Name.Trim();

            contacts[address] = new AccountContact
            {
                Address = address,
                Name = displayName
            };
        }
    }

    private string GetDeltaTokenFromDeltaLink(string deltaLink)
        => Regex.Split(deltaLink, "deltatoken=")[1];

    /// <summary>
    /// Determines MailItemType based on EventMessage's MeetingMessageType.
    /// </summary>
    private static MailItemType GetMailItemType(EventMessage eventMessage)
    {
        if (eventMessage.MeetingMessageType.HasValue)
        {
            return eventMessage.MeetingMessageType.Value switch
            {
                MeetingMessageType.MeetingRequest => MailItemType.CalendarInvitation,
                MeetingMessageType.MeetingCancelled => MailItemType.CalendarCancellation,
                MeetingMessageType.MeetingAccepted or
                MeetingMessageType.MeetingTenativelyAccepted or
                MeetingMessageType.MeetingDeclined => MailItemType.CalendarResponse,
                _ => MailItemType.Mail
            };
        }

        // Fallback to CalendarInvitation if type is unknown
        return MailItemType.CalendarInvitation;
    }

    protected override async Task<MailCopy> CreateMinimalMailCopyAsync(Message message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        // Use centralized method
        return await CreateMailCopyFromMessageAsync(message, assignedFolder).ConfigureAwait(false);
    }

    private async Task<Message> GetMessageByIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await _graphClient.Me.Messages[messageId].GetAsync((config) =>
            {
                config.QueryParameters.Select = outlookMessageSelectParameters;
            }, cancellationToken).ConfigureAwait(false);

            // Check if this is an EventMessage and fetch it separately if needed (only if calendar access granted)
            if (Account.IsCalendarAccessGranted && message is EventMessage)
            {
                message = await FetchEventMessageAsync(message.Id, cancellationToken).ConfigureAwait(false);
            }

            return message;
        }
        catch (ServiceException serviceException)
        {
            // Try to handle the error using the error handling factory first
            var errorContext = new SynchronizerErrorContext
            {
                Account = Account,
                ErrorCode = (int?)serviceException.ResponseStatusCode,
                ErrorMessage = $"Service error during message retrieval: {serviceException.Message}",
                Exception = serviceException
            };

            var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

            if (!handled)
            {
                // No handler could process this error, log and handle appropriately
                if (serviceException.ResponseStatusCode == 404)
                {
                    // Re-throw 404 errors to be handled by the caller for queue cleanup
                    throw;
                }
                else
                {
                    _logger.Error(serviceException, "Unhandled service error while getting message {MessageId}. Error: {ErrorCode}", messageId, serviceException.ResponseStatusCode);
                    return null;
                }
            }
            else
            {
                _logger.Information("Service error handled successfully during message retrieval. Message: {MessageId}, Error: {ErrorCode}", messageId, serviceException.ResponseStatusCode);
                return null; // Return null since the error was handled but we couldn't get the message
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get message {MessageId}", messageId);
            return null;
        }
    }

    private async Task ProcessDeltaChangesAsync(MailItemFolder folder, List<string> downloadedMessageIds, CancellationToken cancellationToken = default)
    {
        // Only process delta changes if we have a delta token (not initial sync)
        if (string.IsNullOrEmpty(folder.DeltaToken))
            return;

        try
        {
            var currentDeltaToken = folder.DeltaToken;

            // Always use Delta endpoint with proper configuration
            var requestInformation = _graphClient.Me.MailFolders[folder.RemoteFolderId].Messages.Delta.ToGetRequestInformation((config) =>
            {
                config.QueryParameters.Select = outlookMessageSelectParameters;
                config.QueryParameters.Orderby = ["receivedDateTime desc"]; // Sort by received date desc
            });

            requestInformation.UrlTemplate = requestInformation.UrlTemplate.Insert(requestInformation.UrlTemplate.Length - 1, ",%24deltatoken");
            requestInformation.QueryParameters.Add("%24deltatoken", currentDeltaToken);

            var messageCollectionPage = await _graphClient.RequestAdapter.SendAsync(requestInformation,
                DeltaGetResponse.CreateFromDiscriminatorValue,
                cancellationToken: cancellationToken);

            // Use PageIterator<DeltaGetResponse> for iterating mails
            var messageIterator = PageIterator<Message, DeltaGetResponse>
                .CreatePageIterator(_graphClient, messageCollectionPage, async (item) =>
                {
                    try
                    {
                        await _handleItemRetrievalSemaphore.WaitAsync();
                        return await HandleItemRetrievedAsync(item, folder, downloadedMessageIds, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error occurred while handling delta item {Id} for folder {FolderName}", item.Id, folder.FolderName);
                    }
                    finally
                    {
                        _handleItemRetrievalSemaphore.Release();
                    }

                    return true;
                });

            await messageIterator.IterateAsync(cancellationToken).ConfigureAwait(false);

            // Update delta token for next sync - store delta token when there are no nextPageToken remaining
            if (!string.IsNullOrEmpty(messageIterator.Deltalink))
            {
                var deltaToken = GetDeltaTokenFromDeltaLink(messageIterator.Deltalink);
                await _outlookChangeProcessor.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, deltaToken).ConfigureAwait(false);
                _logger.Debug("Updated delta token for folder {FolderName} after processing delta changes", folder.FolderName);
            }
        }
        catch (ApiException apiException)
        {
            // Try to handle the error using the error handling factory
            var errorContext = new SynchronizerErrorContext
            {
                Account = Account,
                ErrorCode = (int?)apiException.ResponseStatusCode,
                ErrorMessage = $"API error during legacy delta sync: {apiException.Message}",
                Exception = apiException
            };

            var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

            if (!handled)
            {
                // No handler could process this error, log and re-throw
                _logger.Error(apiException, "Unhandled API error during legacy delta sync for folder {FolderName}. Error: {ErrorCode}", folder.FolderName, apiException.ResponseStatusCode);
            }
        }
    }

    private bool IsResourceDeleted(IDictionary<string, object> additionalData)
        => additionalData != null && additionalData.ContainsKey("@removed");

    /// <summary>
    /// Fetches an EventMessage with full details including MeetingMessageType from the Messages endpoint.
    /// This is necessary because MeetingMessageType is not available when fetching as Message type.
    /// </summary>
    private async Task<EventMessage> FetchEventMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        try
        {
            var requestInfo = _graphClient.Me.Messages[messageId].ToGetRequestInformation((config) =>
            {
                config.QueryParameters.Select = outlookMessageSelectParameters.Concat(["MeetingMessageType"]).ToArray();
            });

            var eventMessage = await _graphClient.Me.Messages[messageId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var odataType = eventMessage?.AdditionalData?.ContainsKey("@odata.type") == true
                ? eventMessage.AdditionalData["@odata.type"]?.ToString()
                : "unknown";

            _logger.Debug("Fetched EventMessage {MessageId} with type {ODataType}", messageId, odataType);

            return eventMessage as EventMessage;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch EventMessage {MessageId}", messageId);
            return null;
        }
    }

    private async Task<bool> HandleFolderRetrievedAsync(MailFolder folder, OutlookSpecialFolderIdInformation outlookSpecialFolderIdInformation, CancellationToken cancellationToken = default)
    {
        if (IsResourceDeleted(folder.AdditionalData))
        {
            await _outlookChangeProcessor.DeleteFolderAsync(Account.Id, folder.Id).ConfigureAwait(false);
            _isFolderStructureChanged = true;
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
            _isFolderStructureChanged = true;
        }

        return true;
    }

    /// <summary>
    /// Somehow, Graph API returns Message type item for items like TodoTask, EventMessage and Contact.
    /// Basically deleted item retention items are stored as Message object in Deleted Items folder.
    /// Suprisingly, odatatype will also be the same as Message.
    /// In order to differentiate them from regular messages, we need to check the addresses in the message.
    /// EventMessage types (calendar invitations/responses) are now processed as regular mail items with appropriate ItemType.
    /// </summary>
    /// <param name="item">Retrieved message.</param>
    /// <returns>Whether the item is non-Message type or not.</returns>
    private bool IsNotRealMessageType(Message item)
        => item.From?.EmailAddress == null;

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
            // Check if this is an EventMessage and fetch it separately if needed (only if calendar access granted)
            if (Account.IsCalendarAccessGranted && item is EventMessage)
            {
                item = await FetchEventMessageAsync(item.Id, cancellationToken).ConfigureAwait(false);
                if (item == null)
                {
                    return true; // Skip this message if fetch failed
                }
            }

            // If the item exists in the local database, it means that it's already downloaded. Process as an Update.

            var isMailExists = await _outlookChangeProcessor.IsMailExistsInFolderAsync(item.Id, folder.Id);

            if (isMailExists)
            {
                // Some of the properties of the item are updated.
                _logger.Debug("Processing delta update for existing mail {MessageId} in folder {FolderName}", item.Id, folder.FolderName);

                if (item.IsRead != null)
                {
                    _logger.Debug("Updating read status for mail {MessageId}: IsRead={IsRead}", item.Id, item.IsRead.GetValueOrDefault());
                    await _outlookChangeProcessor.ChangeMailReadStatusAsync(item.Id, item.IsRead.GetValueOrDefault()).ConfigureAwait(false);
                }

                if (item.Flag?.FlagStatus != null)
                {
                    var isFlagged = item.Flag.FlagStatus.GetValueOrDefault() == FollowupFlagStatus.Flagged;
                    _logger.Debug("Updating flag status for mail {MessageId}: IsFlagged={IsFlagged}", item.Id, isFlagged);
                    await _outlookChangeProcessor.ChangeFlagStatusAsync(item.Id, isFlagged).ConfigureAwait(false);
                }

                if (item.Categories != null)
                {
                    await ReplaceMailAssignmentsAsync(item.Id, item.Categories).ConfigureAwait(false);
                }
            }
            else
            {
                if (IsNotRealMessageType(item))
                {
                    // EventMessages are handled above if calendar access is granted
                    // This catches non-message types like contacts or todo items
                    Log.Warning("Received non-message item type (contact/todo). This is not supported yet. {Id}", item.Id);
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
        _isFolderStructureChanged = false;

        var specialFolderInfo = await GetSpecialFolderIdsAsync(cancellationToken).ConfigureAwait(false);
        var graphFolders = await GetDeltaFoldersAsync(cancellationToken).ConfigureAwait(false);

        var iterator = PageIterator<MailFolder, Microsoft.Graph.Me.MailFolders.Delta.DeltaGetResponse>
            .CreatePageIterator(_graphClient, graphFolders, (folder) =>
                HandleFolderRetrievedAsync(folder, specialFolderInfo, cancellationToken));

        await iterator.IterateAsync();

        await UpdateDeltaSynchronizationIdentifierAsync(iterator.Deltalink).ConfigureAwait(false);

        if (_isFolderStructureChanged)
        {
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
        }
    }

    protected override async Task SynchronizeCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _graphClient.Me.Outlook.MasterCategories
            .GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var categories = response?.Value?
            .Where(a => !string.IsNullOrWhiteSpace(a?.DisplayName))
            .Select(a =>
            {
                var colorOption = GetMailCategoryColorOption(a.Color);

                return new MailCategory
                {
                    MailAccountId = Account.Id,
                    RemoteId = a.Id,
                    Name = a.DisplayName,
                    BackgroundColorHex = colorOption.BackgroundColorHex,
                    TextColorHex = colorOption.TextColorHex,
                    Source = MailCategorySource.Outlook
                };
            })
            .ToList() ?? [];

        await _mailCategoryService.ReplaceCategoriesAsync(Account.Id, categories).ConfigureAwait(false);
    }

    private async Task ReplaceMailAssignmentsAsync(string messageId, IEnumerable<string> categoryNames)
    {
        var localMailCopies = await _outlookChangeProcessor.GetMailCopiesAsync([messageId]).ConfigureAwait(false);

        foreach (var localMailCopy in localMailCopies)
        {
            await _mailCategoryService.ReplaceMailAssignmentsAsync(Account.Id, localMailCopy.UniqueId, categoryNames ?? []).ConfigureAwait(false);
        }
    }

    private async Task<OutlookSpecialFolderIdInformation> GetSpecialFolderIdsAsync(CancellationToken cancellationToken)
    {
        var localFolders = await _outlookChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var cachedSpecialFolders = TryGetSpecialFolderIdsFromLocalFolders(localFolders);

        if (cachedSpecialFolders != null)
        {
            _logger.Debug("Using cached Outlook special folder ids for {AccountName}", Account.Name);
            return cachedSpecialFolders;
        }

        _logger.Information("Cached Outlook special folder ids are incomplete for {AccountName}. Fetching from Microsoft Graph.", Account.Name);

        return new OutlookSpecialFolderIdInformation(
            await GetWellKnownFolderIdAsync(INBOX_NAME, cancellationToken).ConfigureAwait(false),
            await GetWellKnownFolderIdAsync(DELETED_NAME, cancellationToken).ConfigureAwait(false),
            await GetWellKnownFolderIdAsync(JUNK_NAME, cancellationToken).ConfigureAwait(false),
            await GetWellKnownFolderIdAsync(DRAFTS_NAME, cancellationToken).ConfigureAwait(false),
            await GetWellKnownFolderIdAsync(SENT_NAME, cancellationToken).ConfigureAwait(false),
            await GetWellKnownFolderIdAsync(ARCHIVE_NAME, cancellationToken).ConfigureAwait(false));
    }

    private async Task<string> GetWellKnownFolderIdAsync(string wellKnownFolderName, CancellationToken cancellationToken)
    {
        try
        {
            var folder = await _graphClient.Me.MailFolders[wellKnownFolderName]
                .GetAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Select = ["id"];
                }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(folder?.Id))
            {
                throw new SynchronizerException($"Outlook special folder '{wellKnownFolderName}' returned no id.");
            }

            return folder.Id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch Outlook special folder id for {FolderName}", wellKnownFolderName);
            throw;
        }
    }

    private static OutlookSpecialFolderIdInformation TryGetSpecialFolderIdsFromLocalFolders(IEnumerable<MailItemFolder> localFolders)
    {
        if (localFolders == null)
        {
            return null;
        }

        var inboxId = GetSpecialFolderRemoteId(localFolders, SpecialFolderType.Inbox);
        var deletedId = GetSpecialFolderRemoteId(localFolders, SpecialFolderType.Deleted);
        var junkId = GetSpecialFolderRemoteId(localFolders, SpecialFolderType.Junk);
        var draftId = GetSpecialFolderRemoteId(localFolders, SpecialFolderType.Draft);
        var sentId = GetSpecialFolderRemoteId(localFolders, SpecialFolderType.Sent);
        var archiveId = GetSpecialFolderRemoteId(localFolders, SpecialFolderType.Archive);

        if (new[] { inboxId, deletedId, junkId, draftId, sentId, archiveId }.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        return new OutlookSpecialFolderIdInformation(inboxId, deletedId, junkId, draftId, sentId, archiveId);
    }

    private static string GetSpecialFolderRemoteId(IEnumerable<MailItemFolder> localFolders, SpecialFolderType specialFolderType)
        => localFolders.FirstOrDefault(folder => folder.SpecialFolderType == specialFolderType && !string.IsNullOrWhiteSpace(folder.RemoteFolderId))?.RemoteFolderId;

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
        catch (ApiException apiException)
        {
            // Try to handle the error using the error handling factory
            var errorContext = new SynchronizerErrorContext
            {
                Account = Account,
                ErrorCode = (int?)apiException.ResponseStatusCode,
                ErrorMessage = $"API error during folder synchronization: {apiException.Message}",
                Exception = apiException
            };

            var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

            if (handled)
            {
                // The error handler has processed the error (e.g., DeltaTokenExpiredHandler for 410)
                // Update in-memory account state if it was a delta token expiration
                if (apiException.ResponseStatusCode == 410)
                {
                    Account.SynchronizationDeltaIdentifier = string.Empty;
                    _logger.Information("API error handled successfully for account {AccountName} during folder sync. Error: {ErrorCode}", Account.Name, apiException.ResponseStatusCode);
                }
            }
            else
            {
                // No handler could process this error, log and re-throw
                _logger.Error(apiException, "Unhandled API error during folder synchronization for account {AccountName}. Error: {ErrorCode}", Account.Name, apiException.ResponseStatusCode);
                throw;
            }

            // If a handler processed the error and it was 410, retry with fresh token
            if (apiException.ResponseStatusCode == 410)
            {
                return await GetDeltaFoldersAsync(cancellationToken);
            }

            // For other handled errors, we still need to throw since we can't return a meaningful response
            throw;
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

    protected override async Task SynchronizeAliasesAsync()
    {
        var userInfo = await _graphClient.Me.GetAsync((config) =>
        {
            config.QueryParameters.Select = ["mail", "proxyAddresses"];
        }).ConfigureAwait(false);

        var remoteAliases = GetRemoteAliases(userInfo);
        await _outlookChangeProcessor.UpdateRemoteAliasInformationAsync(Account, remoteAliases).ConfigureAwait(false);
    }

    private List<RemoteAccountAlias> GetRemoteAliases(User userInfo)
    {
        var aliases = new Dictionary<string, RemoteAccountAlias>(StringComparer.OrdinalIgnoreCase);

        void AddAlias(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return;

            var normalizedAddress = address.Trim();
            var isAccountAddress = normalizedAddress.Equals(Account.Address, StringComparison.OrdinalIgnoreCase);
            var capability = isAccountAddress ? AliasSendCapability.Confirmed : AliasSendCapability.Unknown;

            if (aliases.TryGetValue(normalizedAddress, out var existingAlias))
            {
                existingAlias.IsPrimary |= isAccountAddress;
                existingAlias.IsRootAlias |= isAccountAddress;

                if (capability == AliasSendCapability.Confirmed)
                    existingAlias.SendCapability = AliasSendCapability.Confirmed;

                return;
            }

            aliases[normalizedAddress] = new RemoteAccountAlias
            {
                AliasAddress = normalizedAddress,
                ReplyToAddress = normalizedAddress,
                IsPrimary = isAccountAddress,
                IsRootAlias = isAccountAddress,
                IsVerified = capability == AliasSendCapability.Confirmed,
                Source = AliasSource.ProviderDiscovered,
                SendCapability = capability
            };
        }

        AddAlias(userInfo?.Mail);

        foreach (var proxyAddress in userInfo?.ProxyAddresses ?? [])
        {
            if (string.IsNullOrWhiteSpace(proxyAddress) ||
                !proxyAddress.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddAlias(proxyAddress["smtp:".Length..]);
        }

        return aliases.Values.ToList();
    }

    /// <summary>
    /// POST requests are handled differently in batches in Graph SDK.
    /// Batch basically ignores the step's coontent-type and body.
    /// Manually create a POST request with empty body and send it.
    /// </summary>
    /// <param name="requestInformation">Post request information.</param>
    /// <param name="content">Content object to serialize.</param>
    /// <returns>Updated post request information.</returns>
    private RequestInformation PreparePostRequestInformation(RequestInformation requestInformation, string contentJson = "{}")
    {
        requestInformation.Headers.Clear();

        requestInformation.Content = new MemoryStream(Encoding.UTF8.GetBytes(contentJson));
        requestInformation.HttpMethod = Method.POST;
        requestInformation.Headers.Add("Content-Type", "application/json");

        return requestInformation;
    }

    private RequestInformation PreparePostRequestInformation(RequestInformation requestInformation, Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody content)
        => PreparePostRequestInformation(requestInformation, JsonSerializer.Serialize(content, OutlookSynchronizerJsonContext.Default.MovePostRequestBody));

    private RequestInformation PrepareReportMessageRequestInformation(ChangeJunkStateRequest request)
    {
        var reportAction = request.IsJunk ? "junk" : "notJunk";
        var body = $$"""
        {
          "IsMessageMoveRequested": true,
          "ReportAction": "{{reportAction}}"
        }
        """;

        return PreparePostRequestInformation(new RequestInformation
        {
            URI = new Uri($"https://graph.microsoft.com/beta/me/messages/{Uri.EscapeDataString(request.Item.Id)}/reportMessage"),
            HttpMethod = Method.POST
        }, body);
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

    public override List<IRequestBundle<RequestInformation>> ChangeJunkState(BatchChangeJunkStateRequest request)
    {
        return request
            .Select(item => (IRequestBundle<RequestInformation>)new HttpRequestBundle<RequestInformation>(
                PrepareReportMessageRequestInformation(item),
                item,
                item))
            .ToList();
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
        var mailCopyId = sendDraftPreparationRequest.MailItem.Id;
        var mimeMessage = sendDraftPreparationRequest.Mime;

        // Graph API ignores the From header in direct MIME uploads, so we must convert
        // to a JSON Message object to properly support sending from aliases.
        var conversationId = sendDraftPreparationRequest.MailItem.ThreadId;
        var outlookMessage = mimeMessage.AsOutlookMessage(false, conversationId);

        var patchDraftRequest = _graphClient.Me.Messages[mailCopyId].ToPatchRequestInformation(outlookMessage);
        var patchDraftBundle = new HttpRequestBundle<RequestInformation>(patchDraftRequest, request);

        var sendRequest = PreparePostRequestInformation(_graphClient.Me.Messages[mailCopyId].Send.ToPostRequestInformation());
        var sendBundle = new HttpRequestBundle<RequestInformation>(sendRequest, request);

        // Attachment uploads are handled outside batching because large attachments
        // require upload sessions whose URLs are generated dynamically.
        return [patchDraftBundle, sendBundle];
    }

    private async Task UploadDraftAttachmentsAsync(SendDraftRequest sendDraftRequest, CancellationToken cancellationToken)
    {
        var mailCopyId = sendDraftRequest.Request.MailItem.Id;
        var attachments = sendDraftRequest.Request.Mime.ExtractAttachments();

        if (!attachments.Any())
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contentBytes = attachment.ContentBytes ?? [];
            if (contentBytes.Length <= SimpleAttachmentUploadLimitBytes)
            {
                await _graphClient.Me.Messages[mailCopyId].Attachments.PostAsync(attachment, cancellationToken: cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (contentBytes.Length > MaximumUploadSessionAttachmentSizeBytes)
            {
                var attachmentSizeMb = contentBytes.LongLength / (1024d * 1024d);
                var maximumSizeMb = MaximumUploadSessionAttachmentSizeBytes / (1024d * 1024d);

                throw new InvalidOperationException(
                    $"Attachment '{attachment.Name}' is {attachmentSizeMb:F1} MB, which exceeds Outlook's upload limit of {maximumSizeMb:F0} MB per attachment.");
            }

            var sessionBody = new Microsoft.Graph.Me.Messages.Item.Attachments.CreateUploadSession.CreateUploadSessionPostRequestBody
            {
                AttachmentItem = new AttachmentItem
                {
                    AttachmentType = AttachmentType.File,
                    ContentType = attachment.ContentType,
                    Name = attachment.Name,
                    Size = contentBytes.LongLength
                }
            };

            var uploadSession = await _graphClient.Me.Messages[mailCopyId].Attachments.CreateUploadSession.PostAsync(sessionBody, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (uploadSession?.UploadUrl == null)
            {
                throw new InvalidOperationException($"Failed to create upload session for attachment '{attachment.Name}'.");
            }

            await UploadAttachmentInChunksAsync(uploadSession.UploadUrl, contentBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UploadAttachmentInChunksAsync(string uploadUrl, byte[] content, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();

        var totalSize = content.Length;
        var offset = 0;

        while (offset < totalSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkLength = Math.Min(LargeAttachmentUploadChunkSizeBytes, totalSize - offset);
            var end = offset + chunkLength - 1;

            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(content, offset, chunkLength)
            };

            request.Content.Headers.Add("Content-Range", $"bytes {offset}-{end}/{totalSize}");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Upload session returns either 202 (continue) or 201/200 (completed).
            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Attachment chunk upload failed with status {(int)response.StatusCode}: {responseContent}");
            }

            offset += chunkLength;
        }
    }

    public override List<IRequestBundle<RequestInformation>> Archive(BatchArchiveRequest request)
    {
        var batchMoveRequest = new BatchMoveRequest(request.Select(item => new MoveRequest(item.Item, item.FromFolder, item.ToFolder)));

        return Move(batchMoveRequest);
    }

    public override List<IRequestBundle<RequestInformation>> UpdateCategories(BatchMailCategoryAssignmentRequest request)
        => ForEachRequest(request, item => CreateMessageCategoryPatchRequest(item.Item.Id, item.CategoryNames));

    public override List<IRequestBundle<RequestInformation>> CreateCategory(MailCategoryCreateRequest request)
    {
        var outlookCategory = new OutlookCategory
        {
            DisplayName = request.Category.Name,
            Color = GetOutlookCategoryColor(request.Category)
        };

        var requestInfo = _graphClient.Me.Outlook.MasterCategories.ToPostRequestInformation(outlookCategory);
        return [new HttpRequestBundle<RequestInformation>(requestInfo, request)];
    }

    public override List<IRequestBundle<RequestInformation>> UpdateCategory(MailCategoryUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PreviousRemoteId))
            return CreateCategory(new MailCategoryCreateRequest(request.Category));

        var hasNameChanged = !string.Equals(request.PreviousName, request.Category.Name, StringComparison.Ordinal);
        if (!hasNameChanged)
        {
            var requestInfo = _graphClient.Me.Outlook.MasterCategories[request.PreviousRemoteId].ToPatchRequestInformation(new OutlookCategory
            {
                Color = GetOutlookCategoryColor(request.Category)
            });

            return [new HttpRequestBundle<RequestInformation>(requestInfo, request)];
        }

        var bundles = new List<IRequestBundle<RequestInformation>>();
        var createRequestInfo = _graphClient.Me.Outlook.MasterCategories.ToPostRequestInformation(new OutlookCategory
        {
            DisplayName = request.Category.Name,
            Color = GetOutlookCategoryColor(request.Category)
        });

        bundles.Add(new HttpRequestBundle<RequestInformation>(createRequestInfo, request));

        foreach (var target in request.AffectedMessages ?? [])
        {
            bundles.Add(new HttpRequestBundle<RequestInformation>(
                CreateMessageCategoryPatchRequest(target.MessageId, target.CategoryNames),
                request));
        }

        bundles.Add(new HttpRequestBundle<RequestInformation>(
            _graphClient.Me.Outlook.MasterCategories[request.PreviousRemoteId].ToDeleteRequestInformation(),
            request));

        return bundles;
    }

    public override List<IRequestBundle<RequestInformation>> DeleteCategory(MailCategoryDeleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PreviousRemoteId))
            return [];

        var bundles = new List<IRequestBundle<RequestInformation>>();

        foreach (var target in request.AffectedMessages ?? [])
        {
            bundles.Add(new HttpRequestBundle<RequestInformation>(
                CreateMessageCategoryPatchRequest(target.MessageId, target.CategoryNames),
                request));
        }

        bundles.Add(new HttpRequestBundle<RequestInformation>(
            _graphClient.Me.Outlook.MasterCategories[request.PreviousRemoteId].ToDeleteRequestInformation(),
            request));

        return bundles;
    }

    private RequestInformation CreateMessageCategoryPatchRequest(string messageId, IReadOnlyList<string> categoryNames)
        => _graphClient.Me.Messages[messageId].ToPatchRequestInformation(new Message
        {
            Categories = categoryNames?.ToList() ?? []
        });

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem,
                                                           MailKit.ITransferProgress transferProgress = null,
                                                           CancellationToken cancellationToken = default)
    {
        try
        {
            var mimeMessage = await DownloadMimeMessageAsync(mailItem.Id, cancellationToken).ConfigureAwait(false);
            await _outlookChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            _logger.Warning("Outlook message {MailId} not found (404) during MIME download. Deleting locally.", mailItem.Id);
            await _outlookChangeProcessor.DeleteMailAsync(Account.Id, mailItem.Id).ConfigureAwait(false);
            throw new SynchronizerEntityNotFoundException(ex.Message);
        }
    }

    public override async Task DownloadCalendarAttachmentAsync(
        Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem,
        Wino.Core.Domain.Entities.Calendar.CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var calendar = calendarItem.AssignedCalendar;
            var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();

            // First, get the attachment metadata to retrieve contentBytes for FileAttachment
            var attachmentItem = await _graphClient.Me
                .Calendars[calendar.RemoteCalendarId]
                .Events[remoteEventId]
                .Attachments[attachment.RemoteAttachmentId]
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (attachmentItem == null)
            {
                _logger.Error("Failed to retrieve attachment {AttachmentId} for event {EventId}", attachment.RemoteAttachmentId, calendarItem.RemoteEventId);
                throw new InvalidOperationException("Failed to retrieve attachment.");
            }

            byte[] contentBytes = null;

            // Handle FileAttachment (has ContentBytes property)
            if (attachmentItem is FileAttachment fileAttachment && fileAttachment.ContentBytes != null)
            {
                contentBytes = fileAttachment.ContentBytes;
            }
            // Handle ItemAttachment (embedded items like emails)
            else if (attachmentItem is ItemAttachment)
            {
                _logger.Warning("ItemAttachment type not supported for download. AttachmentId: {AttachmentId}", attachment.RemoteAttachmentId);
                throw new NotSupportedException("ItemAttachment downloads are not currently supported.");
            }
            else
            {
                _logger.Error("Unknown attachment type or missing content for {AttachmentId}", attachment.RemoteAttachmentId);
                throw new InvalidOperationException("Attachment content is not available.");
            }

            // Save to local file
            await System.IO.File.WriteAllBytesAsync(localFilePath, contentBytes, cancellationToken).ConfigureAwait(false);

            _logger.Information("Downloaded calendar attachment {FileName} to {LocalPath}", attachment.FileName, localFilePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error downloading calendar attachment {AttachmentId}", attachment.Id);
            throw;
        }
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

    public override List<IRequestBundle<RequestInformation>> DeleteFolder(DeleteFolderRequest request)
    {
        var networkCall = _graphClient.Me.MailFolders[request.Folder.RemoteFolderId].ToDeleteRequestInformation();
        return [new HttpRequestBundle<RequestInformation>(networkCall, request)];
    }

    public override List<IRequestBundle<RequestInformation>> CreateSubFolder(CreateSubFolderRequest request)
    {
        var requestBody = new MailFolder
        {
            DisplayName = request.NewFolderName
        };

        var networkCall = _graphClient.Me.MailFolders[request.Folder.RemoteFolderId].ChildFolders.ToPostRequestInformation(requestBody);
        return [new HttpRequestBundle<RequestInformation>(networkCall, request)];
    }

    public override List<IRequestBundle<RequestInformation>> CreateRootFolder(CreateRootFolderRequest request)
    {
        var requestBody = new MailFolder
        {
            DisplayName = request.NewFolderName
        };

        var networkCall = _graphClient.Me.MailFolders.ToPostRequestInformation(requestBody);
        return [new HttpRequestBundle<RequestInformation>(networkCall, request)];
    }

    #endregion

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<RequestInformation>> batchedRequests, CancellationToken cancellationToken = default)
    {
        // First apply all UI changes immediately before any batching.
        // This ensures UI reflects changes right away, regardless of batch processing.
        ApplyOptimisticUiChanges(batchedRequests);

        // SendDraft requests may include large attachments, which require upload sessions.
        // Upload these attachments before the batched patch/send sequence.
        foreach (var sendDraftBundle in batchedRequests.Where(b => b.UIChangeRequest is SendDraftRequest))
        {
            var sendDraftRequest = sendDraftBundle.UIChangeRequest as SendDraftRequest;

            try
            {
                await UploadDraftAttachmentsAsync(sendDraftRequest, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                sendDraftRequest?.RevertUIChanges();
                throw;
            }
        }

        var directRequests = batchedRequests
            .Where(bundle => bundle.Request is ChangeJunkStateRequest)
            .ToList();

        foreach (var directRequest in directRequests)
        {
            try
            {
                await _graphClient.RequestAdapter.SendAsync(
                    directRequest.NativeRequest,
                    Message.CreateFromDiscriminatorValue,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                directRequest.UIChangeRequest?.RevertUIChanges();
                throw;
            }
        }

        // Now batch and execute the network requests.
        var batchEligibleRequests = batchedRequests
            .Except(directRequests)
            .ToList();

        var batchedGroups = batchEligibleRequests.Batch((int)MaximumAllowedBatchRequestSize);

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
            requiresSerial |= bundle.UIChangeRequest is SendDraftRequest
                              or MailCategoryUpdateRequest
                              or MailCategoryDeleteRequest;

            // UI changes are already applied in ExecuteNativeRequestsAsync before batching.
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
                else
                {
                    await HandleSuccessfulResponseAsync(bundle, response).ConfigureAwait(false);
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

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            string.Equals(errorCode, "ErrorSendAsDenied", StringComparison.OrdinalIgnoreCase) &&
            bundle?.UIChangeRequest is SendDraftRequest sendDraftRequest)
        {
            var sendingAlias = sendDraftRequest.Request.SendingAlias?.AliasAddress ?? Account.Address;
            await _outlookChangeProcessor.UpdateAliasSendCapabilityAsync(Account.Id, sendingAlias, AliasSendCapability.Denied).ConfigureAwait(false);
            errorMessage = string.Format(Translator.Exception_AliasSendDenied_Message, sendingAlias);
        }

        var errorString = $"[{response.StatusCode}] {errorCode} - {errorMessage}\n";

        // Create error context
        var errorContext = new SynchronizerErrorContext
        {
            Account = Account,
            ErrorCode = (int)response.StatusCode,
            ErrorMessage = errorMessage,
            RequestBundle = bundle,
            Request = bundle.Request,
            IsEntityNotFound = IsKnownOutlookEntityNotFoundError(response.StatusCode, errorCode, errorMessage, bundle),
            AdditionalData = new Dictionary<string, object>
            {
                { "ErrorCode", errorCode },
                { "HttpResponse", response },
                { "Content", content }
            }
        };

        // Try to handle the error with registered handlers
        var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext);

        // Transient errors still need to bubble so the request can be retried or surfaced to the caller.
        if (!handled || errorContext.Severity == SynchronizerErrorSeverity.Transient)
        {
            CaptureSynchronizationIssue(errorContext);
            bundle.UIChangeRequest?.RevertUIChanges();
            Debug.WriteLine(errorString);
            errors.Add(errorString);
        }
    }

    private static bool IsKnownOutlookEntityNotFoundError(
        HttpStatusCode statusCode,
        string errorCode,
        string errorMessage,
        IRequestBundle<RequestInformation> bundle)
    {
        if (statusCode != HttpStatusCode.NotFound || bundle?.UIChangeRequest == null)
            return false;

        if (!IsExistingEntityOperation(bundle.UIChangeRequest))
            return false;

        var normalizedErrorCode = errorCode?.Trim().ToLowerInvariant() ?? string.Empty;
        var normalizedMessage = errorMessage?.Trim().ToLowerInvariant() ?? string.Empty;

        return normalizedErrorCode.Contains("notfound")
               || normalizedErrorCode.Contains("itemnotfound")
               || normalizedErrorCode.Contains("resource")
               || normalizedMessage.Contains("not found")
               || normalizedMessage.Contains("does not exist")
               || normalizedMessage.Contains("cannot be found");
    }

    private static bool IsExistingEntityOperation(IUIChangeRequest request)
        => request is BatchDeleteRequest
           || request is BatchMoveRequest
           || request is BatchChangeJunkStateRequest
           || request is BatchChangeFlagRequest
           || request is BatchMarkReadRequest
           || request is BatchArchiveRequest
           || request is DeleteRequest
           || request is MoveRequest
           || request is ChangeJunkStateRequest
           || request is ChangeFlagRequest
           || request is MarkReadRequest
           || request is ArchiveRequest
           || request is MailCategoryAssignmentRequest
           || request is RenameFolderRequest
           || request is MailCategoryUpdateRequest
           || request is MailCategoryDeleteRequest
           || request is DeleteFolderRequest
           || request is AcceptEventRequest
           || request is DeclineEventRequest
           || request is OutlookDeclineEventRequest
           || request is TentativeEventRequest
           || request is UpdateCalendarEventRequest
           || request is DeleteCalendarEventRequest;

    private async Task HandleSuccessfulResponseAsync(IRequestBundle<RequestInformation> bundle, HttpResponseMessage response)
    {
        try
        {
            if (bundle?.UIChangeRequest is MarkReadRequest markReadRequest)
            {
                await _outlookChangeProcessor.ChangeMailReadStatusAsync(markReadRequest.Item.Id, markReadRequest.IsRead).ConfigureAwait(false);
                return;
            }

            if (bundle?.UIChangeRequest is ChangeFlagRequest changeFlagRequest)
            {
                await _outlookChangeProcessor.ChangeFlagStatusAsync(changeFlagRequest.Item.Id, changeFlagRequest.IsFlagged).ConfigureAwait(false);
                return;
            }

            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return;

            var json = JsonNode.Parse(content);
            if (bundle?.UIChangeRequest is CreateDraftRequest createDraftRequest)
            {
                var createdDraftId = json?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(createdDraftId))
                    return;

                var createdConversationId = json?["conversationId"]?.GetValue<string>();
                var localDraft = createDraftRequest.DraftPreperationRequest.CreatedLocalDraftCopy;

                await _outlookChangeProcessor.MapLocalDraftAsync(
                    Account.Id,
                    localDraft.UniqueId,
                    createdDraftId,
                    createdConversationId,
                    createdConversationId).ConfigureAwait(false);
                return;
            }

            if (bundle?.UIChangeRequest is CreateCalendarEventRequest createCalendarEventRequest)
            {
                var createdEventId = json?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(createdEventId))
                    return;

                await UploadCalendarEventAttachmentsAsync(createCalendarEventRequest, createdEventId, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (bundle?.UIChangeRequest is MailCategoryCreateRequest createCategoryRequest)
            {
                var createdCategoryId = json?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(createdCategoryId))
                {
                    await _mailCategoryService.UpdateRemoteIdAsync(createCategoryRequest.Category.Id, createdCategoryId).ConfigureAwait(false);
                }
                return;
            }

            if (bundle?.UIChangeRequest is MailCategoryUpdateRequest updateCategoryRequest)
            {
                var updatedCategoryId = json?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(updatedCategoryId))
                {
                    await _mailCategoryService.UpdateRemoteIdAsync(updateCategoryRequest.Category.Id, updatedCategoryId).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to process Outlook create response.");
        }
    }

    private async Task UploadCalendarEventAttachmentsAsync(CreateCalendarEventRequest request, string remoteEventId, CancellationToken cancellationToken)
    {
        var attachments = request.ComposeResult.Attachments ?? [];
        if (attachments.Count == 0)
            return;

        var remoteCalendarId = request.AssignedCalendar.RemoteCalendarId;

        foreach (var attachment in attachments.Where(a => !string.IsNullOrWhiteSpace(a.FilePath) && File.Exists(a.FilePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contentBytes = await File.ReadAllBytesAsync(attachment.FilePath, cancellationToken).ConfigureAwait(false);
            var contentType = MimeTypes.GetMimeType(attachment.FileName ?? attachment.FilePath);

            var fileAttachment = new FileAttachment
            {
                Name = attachment.FileName,
                ContentType = contentType,
                ContentBytes = contentBytes
            };

            if (contentBytes.Length <= SimpleAttachmentUploadLimitBytes)
            {
                await _graphClient.Me.Calendars[remoteCalendarId].Events[remoteEventId].Attachments.PostAsync(fileAttachment, cancellationToken: cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (contentBytes.Length > MaximumUploadSessionAttachmentSizeBytes)
            {
                var attachmentSizeMb = contentBytes.LongLength / (1024d * 1024d);
                var maximumSizeMb = MaximumUploadSessionAttachmentSizeBytes / (1024d * 1024d);

                throw new InvalidOperationException(
                    $"Attachment '{attachment.FileName}' is {attachmentSizeMb:F1} MB, which exceeds Outlook's upload limit of {maximumSizeMb:F0} MB per attachment.");
            }

            var sessionBody = new Microsoft.Graph.Me.Calendars.Item.Events.Item.Attachments.CreateUploadSession.CreateUploadSessionPostRequestBody
            {
                AttachmentItem = new AttachmentItem
                {
                    AttachmentType = AttachmentType.File,
                    ContentType = contentType,
                    Name = attachment.FileName,
                    Size = contentBytes.LongLength
                }
            };

            var uploadSession = await _graphClient.Me.Calendars[remoteCalendarId].Events[remoteEventId].Attachments.CreateUploadSession.PostAsync(sessionBody, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (uploadSession?.UploadUrl == null)
            {
                throw new InvalidOperationException($"Failed to create upload session for attachment '{attachment.FileName}'.");
            }

            await UploadAttachmentInChunksAsync(uploadSession.UploadUrl, contentBytes, cancellationToken).ConfigureAwait(false);
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
        var messagesById = new Dictionary<string, Message>(StringComparer.Ordinal);

        // Perform search for each folder separately.
        if (folders?.Count > 0)
        {
            var folderIds = folders
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.RemoteFolderId))
                .Select(a => a.RemoteFolderId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var tasks = folderIds.Select(async folderId =>
            {
                var mailQuery = _graphClient.Me.MailFolders[folderId].Messages
                    .GetAsync(requestConfig =>
                    {
                        requestConfig.QueryParameters.Search = $"\"{queryText}\"";
                        requestConfig.QueryParameters.Select = ["Id, ParentFolderId"];
                        requestConfig.QueryParameters.Top = 1000;
                    }, cancellationToken);

                var result = await mailQuery;

                if (result?.Value != null)
                {
                    lock (messagesById)
                    {
                        foreach (var message in result.Value)
                        {
                            if (string.IsNullOrWhiteSpace(message?.Id)) continue;
                            messagesById[message.Id] = message;
                        }
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
                foreach (var message in result.Value)
                {
                    if (string.IsNullOrWhiteSpace(message?.Id)) continue;
                    messagesById[message.Id] = message;
                }
            }
        }

        if (messagesById.Count == 0) return [];

        var localFolders = (await _outlookChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false))
            .ToDictionary(x => x.RemoteFolderId);

        // Contains a list of message ids that potentially can be downloaded.
        var messageIdsWithKnownFolder = new HashSet<string>(StringComparer.Ordinal);

        // Validate that all messages are in a known folder.
        foreach (var message in messagesById.Values)
        {
            if (!localFolders.ContainsKey(message.ParentFolderId))
            {
                Log.Warning("Search result returned a message from a folder that is not synchronized.");
                continue;
            }

            messageIdsWithKnownFolder.Add(message.Id);
        }

        if (messageIdsWithKnownFolder.Count == 0) return [];

        var locallyExistingMails = await _outlookChangeProcessor.AreMailsExistsAsync(messageIdsWithKnownFolder).ConfigureAwait(false);
        var existingMessageIds = new HashSet<string>(locallyExistingMails, StringComparer.Ordinal);

        // Find messages that are not downloaded yet.
        List<Message> messagesToDownload = [];
        foreach (var id in messageIdsWithKnownFolder.Except(existingMessageIds, StringComparer.Ordinal))
        {
            if (messagesById.TryGetValue(id, out var message))
            {
                messagesToDownload.Add(message);
            }
        }

        foreach (var message in messagesToDownload)
        {
            await DownloadSearchResultMessageAsync(message.Id, localFolders[message.ParentFolderId], existingMessageIds, cancellationToken).ConfigureAwait(false);
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
        // Download MIME message for specific scenarios (e.g., search results, draft handling)
        // During normal sync, this method should not be called - use CreateMailCopyFromMessageAsync instead
        var mimeMessage = await DownloadMimeMessageAsync(message.Id, cancellationToken).ConfigureAwait(false);
        var mailCopy = await CreateMailCopyFromMessageAsync(message, assignedFolder).ConfigureAwait(false);

        // If draft mapping was successful, mailCopy will be null
        if (mailCopy == null) return null;

        await TryMapCalendarInvitationAsync(mailCopy, mimeMessage, cancellationToken).ConfigureAwait(false);

        // Outlook messages can only be assigned to 1 folder at a time.
        // Therefore we don't need to create multiple copies of the same message for different folders.
        var contacts = ExtractContactsFromOutlookMessage(message);
        var package = new NewMailItemPackage(mailCopy, mimeMessage, assignedFolder.RemoteFolderId, contacts, message.Categories);

        return [package];
    }

    private static MailCategoryColorOption GetMailCategoryColorOption(CategoryColor? color)
        => color switch
        {
            CategoryColor.Preset0 => new("#FEE2E2", "#991B1B"),
            CategoryColor.Preset1 => new("#FFEDD5", "#9A3412"),
            CategoryColor.Preset2 => new("#FEF3C7", "#92400E"),
            CategoryColor.Preset3 => new("#ECFCCB", "#3F6212"),
            CategoryColor.Preset4 => new("#DCFCE7", "#166534"),
            CategoryColor.Preset5 => new("#CCFBF1", "#115E59"),
            CategoryColor.Preset6 => new("#CFFAFE", "#155E75"),
            CategoryColor.Preset7 => new("#DBEAFE", "#1D4ED8"),
            CategoryColor.Preset8 => new("#E0E7FF", "#4338CA"),
            CategoryColor.Preset9 => new("#F3E8FF", "#7E22CE"),
            CategoryColor.Preset10 => new("#FCE7F3", "#9D174D"),
            CategoryColor.Preset11 => new("#FECACA", "#7F1D1D"),
            CategoryColor.Preset12 => new("#FED7AA", "#7C2D12"),
            CategoryColor.Preset13 => new("#FDE68A", "#78350F"),
            CategoryColor.Preset14 => new("#D9F99D", "#365314"),
            CategoryColor.Preset15 => new("#BBF7D0", "#14532D"),
            CategoryColor.Preset16 => new("#99F6E4", "#134E4A"),
            CategoryColor.Preset17 => new("#A5F3FC", "#164E63"),
            CategoryColor.Preset18 => new("#BFDBFE", "#1E3A8A"),
            CategoryColor.Preset19 => new("#DDD6FE", "#5B21B6"),
            CategoryColor.Preset20 => new("#E5E7EB", "#374151"),
            CategoryColor.Preset21 => new("#D1D5DB", "#1F2937"),
            CategoryColor.Preset22 => new("#F3F4F6", "#111827"),
            CategoryColor.Preset23 => new("#E2E8F0", "#334155"),
            CategoryColor.Preset24 => new("#F8FAFC", "#475569"),
            _ => new("#E5E7EB", "#374151")
        };

    private static CategoryColor GetOutlookCategoryColor(MailCategory category)
        => (category.BackgroundColorHex?.ToUpperInvariant(), category.TextColorHex?.ToUpperInvariant()) switch
        {
            ("#FEE2E2", "#991B1B") => CategoryColor.Preset0,
            ("#FFEDD5", "#9A3412") => CategoryColor.Preset1,
            ("#FEF3C7", "#92400E") => CategoryColor.Preset2,
            ("#ECFCCB", "#3F6212") => CategoryColor.Preset3,
            ("#DCFCE7", "#166534") => CategoryColor.Preset4,
            ("#CCFBF1", "#115E59") => CategoryColor.Preset5,
            ("#CFFAFE", "#155E75") => CategoryColor.Preset6,
            ("#DBEAFE", "#1D4ED8") => CategoryColor.Preset7,
            ("#E0E7FF", "#4338CA") => CategoryColor.Preset8,
            ("#F3E8FF", "#7E22CE") => CategoryColor.Preset9,
            ("#FCE7F3", "#9D174D") => CategoryColor.Preset10,
            ("#FECACA", "#7F1D1D") => CategoryColor.Preset11,
            ("#FED7AA", "#7C2D12") => CategoryColor.Preset12,
            ("#FDE68A", "#78350F") => CategoryColor.Preset13,
            ("#D9F99D", "#365314") => CategoryColor.Preset14,
            ("#BBF7D0", "#14532D") => CategoryColor.Preset15,
            ("#99F6E4", "#134E4A") => CategoryColor.Preset16,
            ("#A5F3FC", "#164E63") => CategoryColor.Preset17,
            ("#BFDBFE", "#1E3A8A") => CategoryColor.Preset18,
            ("#DDD6FE", "#5B21B6") => CategoryColor.Preset19,
            _ => CategoryColor.Preset0
        };

    private async Task TryMapCalendarInvitationAsync(MailCopy mailCopy, MimeMessage mimeMessage, CancellationToken cancellationToken)
    {
        if (mailCopy.ItemType != MailItemType.CalendarInvitation || mimeMessage == null)
            return;

        var invitationUid = mimeMessage.ExtractInvitationUid();
        if (string.IsNullOrWhiteSpace(invitationUid))
            return;

        var calendars = await _outlookChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        if (calendars == null || calendars.Count == 0)
            return;

        string escapedUid = invitationUid.Replace("'", "''", StringComparison.Ordinal);

        foreach (var calendar in calendars)
        {
            try
            {
                var eventsResponse = await _graphClient.Me.Calendars[calendar.RemoteCalendarId].Events
                    .GetAsync(requestConfiguration =>
                    {
                        requestConfiguration.QueryParameters.Filter = $"iCalUId eq '{escapedUid}'";
                        requestConfiguration.QueryParameters.Select = ["id"];
                        requestConfiguration.QueryParameters.Top = 1;
                    }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var matchedEvent = eventsResponse?.Value?.FirstOrDefault();
                if (matchedEvent == null || string.IsNullOrWhiteSpace(matchedEvent.Id))
                    continue;

                var fullEvent = await _graphClient.Me.Calendars[calendar.RemoteCalendarId].Events[matchedEvent.Id]
                    .GetAsync(requestConfiguration =>
                    {
                        requestConfiguration.QueryParameters.Expand = ["attachments($select=id,name,contentType,size,isInline)"];
                    }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (fullEvent == null)
                    continue;

                await _outlookChangeProcessor.ManageCalendarEventAsync(fullEvent, calendar, Account).ConfigureAwait(false);

                var localCalendarItem = await _outlookChangeProcessor.GetCalendarItemAsync(calendar.Id, fullEvent.Id).ConfigureAwait(false);
                if (localCalendarItem == null)
                    return;

                await _outlookChangeProcessor.UpsertMailInvitationCalendarMappingAsync(new MailInvitationCalendarMapping()
                {
                    Id = Guid.NewGuid(),
                    AccountId = Account.Id,
                    MailCopyId = mailCopy.Id,
                    InvitationUid = invitationUid,
                    CalendarId = calendar.Id,
                    CalendarItemId = localCalendarItem.Id,
                    CalendarRemoteEventId = fullEvent.Id
                }).ConfigureAwait(false);

                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to map Outlook calendar invitation mail {MailCopyId} for calendar {CalendarId}", mailCopy.Id, calendar.Id);
            }
        }
    }

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        _logger.Information("Internal calendar synchronization started for {Name}", Account.Name);

        cancellationToken.ThrowIfCancellationRequested();

        await SynchronizeCalendarsAsync(cancellationToken).ConfigureAwait(false);

        if (options?.Type == CalendarSynchronizationType.CalendarMetadata)
            return CalendarSynchronizationResult.Empty;

        var localCalendars = (await _outlookChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false))
            .Where(c => c.IsSynchronizationEnabled)
            .ToList();

        Microsoft.Graph.Me.Calendars.Item.CalendarView.Delta.DeltaGetResponse eventsDeltaResponse = null;

        // TODO: Maybe we can batch each calendar?

        var totalCalendars = localCalendars.Count;
        if (totalCalendars > 0)
        {
            UpdateSyncProgress(totalCalendars, totalCalendars, Translator.SyncAction_SynchronizingCalendarEvents);
        }

        for (int i = 0; i < totalCalendars; i++)
        {
            var calendar = localCalendars[i];

            try
            {
                bool isInitialSync = string.IsNullOrEmpty(calendar.SynchronizationDeltaToken);

                if (isInitialSync)
                {
                    _logger.Information("No calendar sync identifier for calendar {Name}. Performing initial sync.", calendar.Name);

                    // ISO 8601 format as expected by Microsoft Graph API (e.g., "2019-11-08T19:00:00-08:00")
                    var startDate = DateTimeOffset.Now.AddYears(-2).ToString("yyyy-MM-ddTHH:mm:sszzz");
                    var endDate = DateTimeOffset.Now.AddYears(2).ToString("yyyy-MM-ddTHH:mm:sszzz");

                    // Get Id only. We will always download the full event.
                    eventsDeltaResponse = await _graphClient.Me.Calendars[calendar.RemoteCalendarId].CalendarView.Delta.GetAsDeltaGetResponseAsync((requestConfiguration) =>
                    {
                        requestConfiguration.QueryParameters.Select = ["id", "type"];
                        requestConfiguration.QueryParameters.StartDateTime = startDate;
                        requestConfiguration.QueryParameters.EndDateTime = endDate;
                    }, cancellationToken: cancellationToken);
                }
                else
                {
                    var currentDeltaToken = calendar.SynchronizationDeltaToken;

                    _logger.Information("Performing delta sync for calendar {Name}.", calendar.Name);

                    var requestInformation = _graphClient.Me.Calendars[calendar.RemoteCalendarId].CalendarView.Delta.ToGetRequestInformation();

                    requestInformation.UrlTemplate = requestInformation.UrlTemplate.Insert(requestInformation.UrlTemplate.Length - 1, ",%24deltatoken");
                    requestInformation.QueryParameters.Add("%24deltatoken", currentDeltaToken);

                    eventsDeltaResponse = await _graphClient.RequestAdapter.SendAsync(requestInformation, Microsoft.Graph.Me.Calendars.Item.CalendarView.Delta.DeltaGetResponse.CreateFromDiscriminatorValue);
                }

                List<Event> events = new();

                // We must first save the parent recurring events to not lose exceptions.
                // Therefore, order the existing items by their type and save the parent recurring events first.

                var messageIteratorAsync = PageIterator<Event, Microsoft.Graph.Me.Calendars.Item.CalendarView.Delta.DeltaGetResponse>.CreatePageIterator(_graphClient, eventsDeltaResponse, (item) =>
                {
                    // Include all event types: SingleInstance, SeriesMaster, Occurrence, and Exception
                    // CalendarView already expands recurring events into individual occurrences
                    events.Add(item);

                    return true;
                });

                await messageIteratorAsync
                    .IterateAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Desc-order will move parent recurring events to the top.
                events = events.OrderByDescending(a => a.Type).ToList();

                _logger.Information("Found {Count} events in total.", events.Count);

                foreach (var item in events)
                {
                    // Declined events are returned as Deleted from the API.
                    // There is no way to distinguish unfortunately atm.

                    if (IsResourceDeleted(item.AdditionalData))
                    {
                        await _outlookChangeProcessor.DeleteCalendarItemAsync(item.Id, calendar.Id).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await _handleCalendarEventRetrievalSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                        Event fullEvent = await _graphClient.Me.Calendars[calendar.RemoteCalendarId].Events[item.Id]
                            .GetAsync(requestConfiguration =>
                            {
                                // Expand attachments but only get metadata, not the full content
                                requestConfiguration.QueryParameters.Expand = new[] { "attachments($select=id,name,contentType,size,isInline)" };
                            }, cancellationToken: cancellationToken).ConfigureAwait(false);
                        await _outlookChangeProcessor.ManageCalendarEventAsync(fullEvent, calendar, Account).ConfigureAwait(false);
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
                            CalendarId = calendar.Id,
                            CalendarName = calendar.Name,
                            OperationType = "CalendarEventSync",
                            Severity = SynchronizerErrorSeverity.Recoverable
                        };

                        _ = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                        CaptureSynchronizationIssue(errorContext);
                        _logger.Error(ex, "Error occurred while handling item {Id} for calendar {Name}", item.Id, calendar.Name);
                    }
                    finally
                    {
                        _handleCalendarEventRetrievalSemaphore.Release();
                    }
                }

                var latestDeltaLink = messageIteratorAsync.Deltalink;

                //Store delta link for tracking new changes.
                if (!string.IsNullOrEmpty(latestDeltaLink))
                {
                    // Parse Delta Token from Delta Link since v5 of Graph SDK works based on the token, not the link.

                    var deltaToken = GetDeltaTokenFromDeltaLink(latestDeltaLink);

                    await _outlookChangeProcessor.UpdateCalendarDeltaSynchronizationToken(calendar.Id, deltaToken).ConfigureAwait(false);
                }

                UpdateSyncProgress(totalCalendars, totalCalendars - (i + 1), Translator.SyncAction_SynchronizingCalendarEvents);
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
                    CalendarId = calendar.Id,
                    CalendarName = calendar.Name,
                    OperationType = "CalendarSync"
                };

                _ = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                CaptureSynchronizationIssue(errorContext);

                if (!errorContext.CanContinueSync)
                    throw;

                UpdateSyncProgress(totalCalendars, totalCalendars - (i + 1), Translator.SyncAction_SynchronizingCalendarEvents);
            }
        }

        // TODO: Return proper results.
        return CalendarSynchronizationResult.Empty;
    }

    private async Task SynchronizeCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var calendars = await _graphClient.Me.Calendars.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var remotePrimaryCalendarId = await GetPrimaryCalendarIdAsync(calendars.Value, cancellationToken).ConfigureAwait(false);

        var localCalendars = await _outlookChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        var usedCalendarColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                var remoteBackgroundColor = GetRemoteOutlookCalendarBackgroundColor(calendar);
                var fallbackColor = ColorHelpers.GetDistinctFlatColorHex(usedCalendarColors, remoteBackgroundColor);
                var localCalendar = calendar.AsCalendar(Account, fallbackColor);
                localCalendar.IsPrimary = string.Equals(localCalendar.RemoteCalendarId, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
                localCalendar.BackgroundColorHex = ResolveSynchronizedCalendarBackgroundColor(remoteBackgroundColor, localCalendar, usedCalendarColors);
                localCalendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(localCalendar.BackgroundColorHex);
                usedCalendarColors.Add(localCalendar.BackgroundColorHex);
                insertedCalendars.Add(localCalendar);
            }
            else
            {
                // Update existing calendar. Right now we only update the name.
                var resolvedColor = ResolveSynchronizedCalendarBackgroundColor(GetRemoteOutlookCalendarBackgroundColor(calendar), existingLocalCalendar, usedCalendarColors);
                if (ShouldUpdateCalendar(calendar, existingLocalCalendar, remotePrimaryCalendarId) ||
                    !string.Equals(existingLocalCalendar.BackgroundColorHex, resolvedColor, StringComparison.OrdinalIgnoreCase))
                {
                    existingLocalCalendar.Name = calendar.Name;
                    existingLocalCalendar.IsPrimary = string.Equals(existingLocalCalendar.RemoteCalendarId, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
                    existingLocalCalendar.IsReadOnly = !calendar.CanEdit.GetValueOrDefault(true);
                    existingLocalCalendar.BackgroundColorHex = resolvedColor;
                    existingLocalCalendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(existingLocalCalendar.BackgroundColorHex);

                    updatedCalendars.Add(existingLocalCalendar);
                }
                else
                {
                    // Remove it from the local folder list to skip additional calendar updates.
                    localCalendars.Remove(existingLocalCalendar);
                }

                usedCalendarColors.Add(resolvedColor);
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

    private bool ShouldUpdateCalendar(Calendar calendar, AccountCalendar accountCalendar, string remotePrimaryCalendarId)
    {
        var remoteCalendarName = calendar.Name;
        var remoteBackgroundColor = ResolveSynchronizedCalendarBackgroundColor(GetRemoteOutlookCalendarBackgroundColor(calendar), accountCalendar);
        var remoteIsPrimary = string.Equals(calendar.Id, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
        var remoteIsReadOnly = !calendar.CanEdit.GetValueOrDefault(true);

        bool isNameChanged = !string.Equals(accountCalendar.Name, remoteCalendarName, StringComparison.OrdinalIgnoreCase);
        bool isBackgroundColorChanged = !string.Equals(accountCalendar.BackgroundColorHex, remoteBackgroundColor, StringComparison.OrdinalIgnoreCase);
        bool isPrimaryChanged = accountCalendar.IsPrimary != remoteIsPrimary;
        bool isReadOnlyChanged = accountCalendar.IsReadOnly != remoteIsReadOnly;

        return isNameChanged || isBackgroundColorChanged || isPrimaryChanged || isReadOnlyChanged;
    }

    private static string GetRemoteOutlookCalendarBackgroundColor(Calendar calendar)
        => string.IsNullOrWhiteSpace(calendar?.HexColor) ? null : calendar.HexColor;

    private static string ResolveSynchronizedCalendarBackgroundColor(
        string remoteBackgroundColor,
        AccountCalendar accountCalendar,
        ISet<string> usedCalendarColors = null)
    {
        if (accountCalendar.IsBackgroundColorUserOverridden)
            return accountCalendar.BackgroundColorHex;

        var preferredColor = string.IsNullOrWhiteSpace(remoteBackgroundColor)
            ? accountCalendar.BackgroundColorHex
            : remoteBackgroundColor;

        return string.IsNullOrWhiteSpace(remoteBackgroundColor) && usedCalendarColors != null
            ? ColorHelpers.GetDistinctFlatColorHex(usedCalendarColors, preferredColor)
            : preferredColor;
    }

    private async Task<string> GetPrimaryCalendarIdAsync(IList<Calendar> remoteCalendars, CancellationToken cancellationToken)
    {
        if (remoteCalendars == null || remoteCalendars.Count == 0)
            return string.Empty;

        var explicitPrimary = remoteCalendars.FirstOrDefault(c => c.IsDefaultCalendar.GetValueOrDefault());
        if (explicitPrimary != null)
            return explicitPrimary.Id;

        try
        {
            var meCalendar = await _graphClient.Me.Calendar.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(meCalendar?.Id))
                return meCalendar.Id;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch default Outlook calendar for {Name}. Falling back to first available calendar.", Account.Name);
        }

        return remoteCalendars.First().Id;
    }

    #region Calendar Operations

    public override List<IRequestBundle<RequestInformation>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var calendarItem = request.PreparedItem;
        var attendees = request.PreparedEvent.Attendees;
        var reminders = request.PreparedEvent.Reminders;
        var calendar = request.AssignedCalendar;

        var outlookEvent = new Microsoft.Graph.Models.Event
        {
            Subject = calendarItem.Title,
            Body = new Microsoft.Graph.Models.ItemBody
            {
                ContentType = Microsoft.Graph.Models.BodyType.Html,
                Content = calendarItem.Description
            },
            Location = new Microsoft.Graph.Models.Location
            {
                DisplayName = calendarItem.Location
            },
            ShowAs = calendarItem.ShowAs switch
            {
                CalendarItemShowAs.Free => Microsoft.Graph.Models.FreeBusyStatus.Free,
                CalendarItemShowAs.Tentative => Microsoft.Graph.Models.FreeBusyStatus.Tentative,
                CalendarItemShowAs.Busy => Microsoft.Graph.Models.FreeBusyStatus.Busy,
                CalendarItemShowAs.OutOfOffice => Microsoft.Graph.Models.FreeBusyStatus.Oof,
                CalendarItemShowAs.WorkingElsewhere => Microsoft.Graph.Models.FreeBusyStatus.WorkingElsewhere,
                _ => Microsoft.Graph.Models.FreeBusyStatus.Busy
            },
            TransactionId = calendarItem.Id.ToString("N")
        };

        if (calendarItem.IsAllDayEvent)
        {
            outlookEvent.IsAllDay = true;
            outlookEvent.Start = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = calendarItem.StartDate.ToString("yyyy-MM-dd"),
                TimeZone = calendarItem.StartTimeZone ?? TimeZoneInfo.Local.Id
            };
            outlookEvent.End = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = calendarItem.EndDate.ToString("yyyy-MM-dd"),
                TimeZone = calendarItem.EndTimeZone ?? calendarItem.StartTimeZone ?? TimeZoneInfo.Local.Id
            };
        }
        else
        {
            outlookEvent.IsAllDay = false;
            outlookEvent.Start = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = calendarItem.StartDate.ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = calendarItem.StartTimeZone ?? TimeZoneInfo.Local.Id
            };
            outlookEvent.End = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = calendarItem.EndDate.ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = calendarItem.EndTimeZone ?? TimeZoneInfo.Local.Id
            };
        }

        if (attendees.Count > 0)
        {
            outlookEvent.Attendees = attendees.Select(a => new Microsoft.Graph.Models.Attendee
            {
                EmailAddress = new Microsoft.Graph.Models.EmailAddress
                {
                    Address = a.Email,
                    Name = a.Name
                },
                Type = a.IsOptionalAttendee ? Microsoft.Graph.Models.AttendeeType.Optional : Microsoft.Graph.Models.AttendeeType.Required
            }).ToList();
        }

        if (reminders.Count > 0)
        {
            var reminder = reminders
                .OrderBy(reminder => reminder.DurationInSeconds)
                .FirstOrDefault(reminder => reminder.ReminderType == CalendarItemReminderType.Popup)
                ?? reminders.OrderBy(reminder => reminder.DurationInSeconds).First();

            outlookEvent.IsReminderOn = true;
            outlookEvent.ReminderMinutesBeforeStart = (int)Math.Max(0, reminder.DurationInSeconds / 60);
        }

        var recurrence = CalendarRecurrenceMapper.CreateOutlookRecurrence(calendarItem);
        if (recurrence != null)
        {
            outlookEvent.Recurrence = recurrence;
        }

        var createRequest = _graphClient.Me.Calendars[calendar.RemoteCalendarId].Events.ToPostRequestInformation(outlookEvent);

        return [new HttpRequestBundle<RequestInformation>(createRequest, request)];
    }

    public override List<IRequestBundle<RequestInformation>> AcceptEvent(AcceptEventRequest request)
    {
        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot accept event without remote event ID");
        }

        var acceptRequestInfo = _graphClient.Me.Calendars[calendar.RemoteCalendarId].Events[remoteEventId].Accept.ToPostRequestInformation(new Microsoft.Graph.Me.Calendars.Item.Events.Item.Accept.AcceptPostRequestBody
        {
            Comment = request.ResponseMessage,
            SendResponse = !string.IsNullOrEmpty(request.ResponseMessage)
        });

        return [new HttpRequestBundle<RequestInformation>(acceptRequestInfo, request)];
    }

    public override List<IRequestBundle<RequestInformation>> OutlookDeclineEvent(OutlookDeclineEventRequest request)
    {
        var responseMessage = request.ResponseMessage;

        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot decline event without remote event ID");
        }

        var declineRequestInfo = _graphClient.Me.Calendars[calendar.RemoteCalendarId].Events[remoteEventId].Decline.ToPostRequestInformation(new Microsoft.Graph.Me.Calendars.Item.Events.Item.Decline.DeclinePostRequestBody
        {
            Comment = responseMessage,
            SendResponse = !string.IsNullOrEmpty(responseMessage)
        });

        return [new HttpRequestBundle<RequestInformation>(declineRequestInfo, request)];
    }

    public override List<IRequestBundle<RequestInformation>> TentativeEvent(TentativeEventRequest request)
    {
        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot tentatively accept event without remote event ID");
        }

        var tentativelyAcceptRequestInfo = _graphClient.Me.Calendars[calendar.RemoteCalendarId].Events[remoteEventId].TentativelyAccept.ToPostRequestInformation(new Microsoft.Graph.Me.Calendars.Item.Events.Item.TentativelyAccept.TentativelyAcceptPostRequestBody
        {
            Comment = request.ResponseMessage,
            SendResponse = !string.IsNullOrEmpty(request.ResponseMessage)
        });

        return [new HttpRequestBundle<RequestInformation>(tentativelyAcceptRequestInfo, request)];
    }

    public override List<IRequestBundle<RequestInformation>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var calendarItem = request.Item;
        var attendees = request.Attendees;

        // Get the calendar for this event
        var calendar = calendarItem.AssignedCalendar;
        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        // Convert CalendarItem to Outlook Event for update
        var outlookEvent = new Microsoft.Graph.Models.Event
        {
            Subject = calendarItem.Title,
            Body = new Microsoft.Graph.Models.ItemBody
            {
                // CalendarItem.Description stores HTML notes content, so updates must preserve it as HTML.
                ContentType = Microsoft.Graph.Models.BodyType.Html,
                Content = calendarItem.Description
            },
            Location = new Microsoft.Graph.Models.Location
            {
                DisplayName = calendarItem.Location
            },
            ShowAs = calendarItem.ShowAs switch
            {
                CalendarItemShowAs.Free => Microsoft.Graph.Models.FreeBusyStatus.Free,
                CalendarItemShowAs.Tentative => Microsoft.Graph.Models.FreeBusyStatus.Tentative,
                CalendarItemShowAs.Busy => Microsoft.Graph.Models.FreeBusyStatus.Busy,
                CalendarItemShowAs.OutOfOffice => Microsoft.Graph.Models.FreeBusyStatus.Oof,
                CalendarItemShowAs.WorkingElsewhere => Microsoft.Graph.Models.FreeBusyStatus.WorkingElsewhere,
                _ => Microsoft.Graph.Models.FreeBusyStatus.Busy
            }
        };

        // Set start and end time using DateTimeTimeZone
        if (calendarItem.IsAllDayEvent)
        {
            // All-day events
            outlookEvent.IsAllDay = true;
            outlookEvent.Start = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = calendarItem.StartDate.ToString("yyyy-MM-dd"),
                TimeZone = "UTC"
            };
            outlookEvent.End = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = calendarItem.EndDate.ToString("yyyy-MM-dd"),
                TimeZone = "UTC"
            };
        }
        else
        {
            // Regular events with time
            // StartDate and EndDate are stored in the event's timezone
            // We preserve the timezone information during update
            outlookEvent.IsAllDay = false;
            outlookEvent.Start = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = calendarItem.StartDate.ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = calendarItem.StartTimeZone ?? TimeZoneInfo.Local.Id
            };
            outlookEvent.End = new Microsoft.Graph.Models.DateTimeTimeZone
            {
                DateTime = calendarItem.EndDate.ToString("yyyy-MM-ddTHH:mm:ss"),
                TimeZone = calendarItem.EndTimeZone ?? TimeZoneInfo.Local.Id
            };
        }

        // Add attendees if any
        if (attendees != null && attendees.Count > 0)
        {
            outlookEvent.Attendees = attendees.Select(a => new Microsoft.Graph.Models.Attendee
            {
                EmailAddress = new Microsoft.Graph.Models.EmailAddress
                {
                    Address = a.Email,
                    Name = a.Name
                },
                Type = a.IsOptionalAttendee ? Microsoft.Graph.Models.AttendeeType.Optional : Microsoft.Graph.Models.AttendeeType.Required
            }).ToList();
        }

        // Update the event using Graph API
        var updateRequest = _graphClient.Me.Events[calendarItem.RemoteEventId.GetProviderRemoteEventId()].ToPatchRequestInformation(outlookEvent);

        return [new HttpRequestBundle<RequestInformation>(updateRequest, request)];
    }

    public override List<IRequestBundle<RequestInformation>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        => UpdateCalendarEvent(request);

    public override List<IRequestBundle<RequestInformation>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
    {
        var calendarItem = request.Item;

        // Get the calendar for this event
        var calendar = calendarItem.AssignedCalendar;
        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
        {
            throw new InvalidOperationException("Cannot delete event without remote event ID");
        }

        var deleteRequest = _graphClient.Me.Calendars[calendar.RemoteCalendarId].Events[remoteEventId].ToDeleteRequestInformation();

        return [new HttpRequestBundle<RequestInformation>(deleteRequest, request)];
    }

    #endregion

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync();

        _graphClient.Dispose();
    }
}

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
using Microsoft.Graph.Me.MailFolders.Item.Messages.Delta;
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
    public override uint InitialMessageDownloadCountPerFolder => 1000;
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

    private readonly SemaphoreSlim _concurrentDownloadSemaphore = new(10); // Limit to 10 concurrent downloads

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

        _logger.Debug("Synchronizing {FolderName} with direct download approach", folder.FolderName);

        // Check if initial sync is completed for this folder
        if (!folder.IsInitialSyncCompleted)
        {
            _logger.Debug("Initial sync not completed for folder {FolderName}. Starting mail synchronization.", folder.FolderName);

            // Download mails for initial sync
            await DownloadMailsForInitialSyncAsync(folder, downloadedMessageIds, cancellationToken).ConfigureAwait(false);

            // Mark initial sync as completed
            await _outlookChangeProcessor.UpdateFolderInitialSyncCompletedAsync(folder.Id, true).ConfigureAwait(false);
            folder.IsInitialSyncCompleted = true;
        }
        else
        {
            // Initial sync is completed, process delta changes and download new mails
            _logger.Debug("Initial sync completed for folder {FolderName}. Processing delta changes and downloading new mails.", folder.FolderName);

            await ProcessDeltaChangesAndDownloadMailsAsync(folder, downloadedMessageIds, cancellationToken).ConfigureAwait(false);
        }

        await _outlookChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

        if (downloadedMessageIds.Any())
        {
            _logger.Information("Downloaded {Count} messages for folder {FolderName}", downloadedMessageIds.Count, folder.FolderName);
        }

        return downloadedMessageIds;
    }

    /// <summary>
    /// Downloads mails for initial synchronization using Delta API and direct download with concurrency control.
    /// </summary>
    private async Task DownloadMailsForInitialSyncAsync(MailItemFolder folder, List<string> downloadedMessageIds, CancellationToken cancellationToken)
    {
        _logger.Debug("Starting initial mail download for folder {FolderName}", folder.FolderName);

        var mailIds = new List<string>();

        try
        {
            // Always use Delta API for initial sync - this ensures proper delta token setup for future incremental syncs
            DeltaGetResponse messageCollectionPage = null;

            if (string.IsNullOrEmpty(folder.DeltaToken))
            {
                messageCollectionPage = await _graphClient.Me.MailFolders[folder.RemoteFolderId].Messages.Delta.GetAsDeltaGetResponseAsync((config) =>
                {
                    config.QueryParameters.Select = ["Id"]; // Only get the message Ids
                    config.QueryParameters.Orderby = ["receivedDateTime desc"]; // Sort by received date desc
                    config.QueryParameters.Top = (int)InitialMessageDownloadCountPerFolder;
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var requestInformation = _graphClient.Me.MailFolders[folder.RemoteFolderId].Messages.Delta.ToGetRequestInformation((config) =>
                {
                    config.QueryParameters.Select = ["Id"]; // Only get the message Ids
                    config.QueryParameters.Orderby = ["receivedDateTime desc"]; // Sort by received date desc
                });

                requestInformation.UrlTemplate = requestInformation.UrlTemplate.Insert(requestInformation.UrlTemplate.Length - 1, ",%24deltatoken");
                requestInformation.QueryParameters.Add("%24deltatoken", folder.DeltaToken);

                messageCollectionPage = await _graphClient.RequestAdapter.SendAsync(requestInformation, DeltaGetResponse.CreateFromDiscriminatorValue, cancellationToken: cancellationToken);
            }

            // Use PageIterator<DeltaGetResponse> for iterating through the messages
            var messageIterator = PageIterator<Message, DeltaGetResponse>.CreatePageIterator(_graphClient, messageCollectionPage, (message) =>
            {
                if (!IsResourceDeleted(message.AdditionalData))
                {
                    mailIds.Add(message.Id);
                }

                // Iterator must continue all the time to recieve delta token at the end.
                return true;
            });

            await messageIterator.IterateAsync(cancellationToken).ConfigureAwait(false);

        // Extract delta token from the iterator's delta link
        string deltaToken = null;
        if (!string.IsNullOrEmpty(messageIterator.Deltalink))
        {
            deltaToken = GetDeltaTokenFromDeltaLink(messageIterator.Deltalink);
        }

            // Download mails concurrently with semaphore control
            if (mailIds.Any())
            {
                _logger.Information("Starting concurrent download of {Count} mails for folder {FolderName}", mailIds.Count, folder.FolderName);
                await DownloadMailsConcurrentlyAsync(mailIds, folder, downloadedMessageIds, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.Information("No mail ids found to download for folder {FolderName}", folder.FolderName);
            }

            // Store the delta token for future incremental syncs - always store when available
            if (!string.IsNullOrEmpty(deltaToken))
            {
                await _outlookChangeProcessor.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, deltaToken).ConfigureAwait(false);
                await _outlookChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);
                folder.DeltaToken = deltaToken;
                _logger.Information("Stored delta token for folder {FolderName} - future syncs will be incremental", folder.FolderName);
            }
            else
            {
                _logger.Warning("No delta token received for folder {FolderName} - future syncs may re-download messages", folder.FolderName);
            }
        }
        catch (ApiException apiException)
        {
            // Try to handle the error using the error handling factory
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
                // The error handler has processed the error (e.g., DeltaTokenExpiredHandler for 410)
                // Update in-memory folder state if it was a delta token expiration
                if (apiException.ResponseStatusCode == 410)
                {
                    folder.DeltaToken = string.Empty;
                    folder.IsInitialSyncCompleted = false;
                    _logger.Information("API error handled successfully for folder {FolderName} during initial sync. Error: {ErrorCode}", folder.FolderName, apiException.ResponseStatusCode);
                }
            }
            else
            {
                // No handler could process this error, log and re-throw
                _logger.Error(apiException, "Unhandled API error during initial sync for folder {FolderName}. Error: {ErrorCode}", folder.FolderName, apiException.ResponseStatusCode);
            }
            
            // Re-throw the exception so the synchronization can be retried
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error occurred during initial mail download for folder {FolderName}", folder.FolderName);
            throw;
        }
    }

    /// <summary>
    /// Downloads mails concurrently with semaphore control to limit concurrent downloads to 10.
    /// </summary>
    private async Task DownloadMailsConcurrentlyAsync(List<string> mailIds, MailItemFolder folder, List<string> downloadedMessageIds, CancellationToken cancellationToken)
    {
        var downloadTasks = mailIds.Select(async mailId =>
        {
            await _concurrentDownloadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var downloaded = await DownloadSingleMailAsync(mailId, folder, cancellationToken).ConfigureAwait(false);
                if (downloaded != null)
                {
                    lock (downloadedMessageIds)
                    {
                        downloadedMessageIds.Add(downloaded);
                    }
                }
            }
            finally
            {
                _concurrentDownloadSemaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a single mail by ID and creates it in the database.
    /// </summary>
    private async Task<string> DownloadSingleMailAsync(string mailId, MailItemFolder folder, CancellationToken cancellationToken)
    {
        try
        {
            // Check if mail already exists in database before downloading
            // to avoid unnecessary API calls and reprocessing existing mails
            bool mailExists = await _outlookChangeProcessor.IsMailExistsInFolderAsync(mailId, folder.Id).ConfigureAwait(false);
            if (mailExists)
            {
                _logger.Debug("Mail {MailId} already exists in folder {FolderName}, skipping download", mailId, folder.FolderName);
                return null; // Not a new download
            }

            // Download the message with minimal properties
            var message = await GetMessageByIdAsync(mailId, cancellationToken).ConfigureAwait(false);

            if (message != null)
            {
                // Create minimal MailCopy without downloading MIME
                var mailCopy = await CreateMinimalMailCopyAsync(message, folder, cancellationToken).ConfigureAwait(false);

                if (mailCopy != null)
                {
                    // Create a minimal package without MIME for direct sync
                    var package = new NewMailItemPackage(mailCopy, null, folder.RemoteFolderId);
                    bool isInserted = await _outlookChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);

                    if (isInserted)
                    {
                        return mailCopy.Id; // Successfully created
                    }
                    else
                    {
                        _logger.Warning("Failed to insert mail {MailId} for folder {FolderName}", mailId, folder.FolderName);
                    }
                }
                else
                {
                    _logger.Debug("Could not create MailCopy for {MailId} in folder {FolderName} (might be unsupported message type)", mailId, folder.FolderName);
                }
            }
            else
            {
                _logger.Debug("Message {MailId} is null for folder {FolderName} (filtered out)", mailId, folder.FolderName);
            }
        }
        catch (ServiceException serviceException)
        {
            // Try to handle the error using the error handling factory first
            var errorContext = new SynchronizerErrorContext
            {
                Account = Account,
                ErrorCode = (int?)serviceException.ResponseStatusCode,
                ErrorMessage = $"Service error during mail download: {serviceException.Message}",
                Exception = serviceException
            };

            var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
            
            if (!handled)
            {
                // No handler could process this error, log appropriately
                if (serviceException.ResponseStatusCode == 404)
                {
                    _logger.Warning("Mail {MailId} not found on server (404) for folder {FolderName}", mailId, folder.FolderName);
                }
                else
                {
                    _logger.Error(serviceException, "Unhandled service error while downloading mail {MailId} for folder {FolderName}. Error: {ErrorCode}", mailId, folder.FolderName, serviceException.ResponseStatusCode);
                }
            }
            else
            {
                _logger.Information("Service error handled successfully during mail download. Mail: {MailId}, Folder: {FolderName}, Error: {ErrorCode}", mailId, folder.FolderName, serviceException.ResponseStatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error occurred while downloading mail {MailId} for folder {FolderName}", mailId, folder.FolderName);
        }

        return null;
    }

    private string GetDeltaTokenFromDeltaLink(string deltaLink)
        => Regex.Split(deltaLink, "deltatoken=")[1];

    protected override async Task QueueMailIdsForInitialSyncAsync(MailItemFolder folder, CancellationToken cancellationToken = default)
    {
        // This method is now replaced by direct downloading logic
        // Instead of queuing mail IDs, we now directly download them with concurrency control
        var downloadedMessageIds = new List<string>();
        await DownloadMailsForInitialSyncAsync(folder, downloadedMessageIds, cancellationToken).ConfigureAwait(false);
    }

    protected override Task<MailCopy> CreateMinimalMailCopyAsync(Message message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        if (message == null) return Task.FromResult<MailCopy>(null);

        // Create MailCopy with minimal properties - no MIME download
        var mailCopy = message.AsMailCopy();
        mailCopy.FolderId = assignedFolder.Id;
        mailCopy.UniqueId = Guid.NewGuid();
        mailCopy.FileId = Guid.NewGuid();

        return Task.FromResult(mailCopy);
    }

    private async Task<Message> GetMessageByIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _graphClient.Me.Messages[messageId].GetAsync((config) =>
            {
                config.QueryParameters.Select = outlookMessageSelectParameters;
            }, cancellationToken).ConfigureAwait(false);
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

    private async Task ProcessDeltaChangesAndDownloadMailsAsync(MailItemFolder folder, List<string> downloadedMessageIds, CancellationToken cancellationToken = default)
    {
        // Process delta changes and directly download new mails
        if (string.IsNullOrEmpty(folder.DeltaToken))
        {
            _logger.Debug("No delta token available for folder {FolderName}. Skipping delta sync.", folder.FolderName);
            return;
        }

        try
        {
            var currentDeltaToken = folder.DeltaToken;

            _logger.Debug("Processing delta changes for folder {FolderName} with token {DeltaToken}", folder.FolderName, currentDeltaToken.Substring(0, Math.Min(10, currentDeltaToken.Length)) + "...");

            // Always use Delta endpoint with proper configuration
            var requestInformation = _graphClient.Me.MailFolders[folder.RemoteFolderId].Messages.Delta.ToGetRequestInformation((config) =>
            {
                config.QueryParameters.Select = ["Id"]; // Only get IDs for direct download
                config.QueryParameters.Orderby = ["receivedDateTime desc"]; // Sort by received date desc
            });

            requestInformation.UrlTemplate = requestInformation.UrlTemplate.Insert(requestInformation.UrlTemplate.Length - 1, ",%24deltatoken");
            requestInformation.QueryParameters.Add("%24deltatoken", currentDeltaToken);

            var messageCollectionPage = await _graphClient.RequestAdapter.SendAsync(requestInformation,
                DeltaGetResponse.CreateFromDiscriminatorValue,
                cancellationToken: cancellationToken);

            var newMailIds = new List<string>();

            // Use PageIterator<DeltaGetResponse> for iterating through delta changes
            var messageIterator = PageIterator<Message, DeltaGetResponse>
                .CreatePageIterator(_graphClient, messageCollectionPage, (message) =>
                {
                    // Only process new messages, not deleted ones
                    if (!IsResourceDeleted(message.AdditionalData))
                    {
                        newMailIds.Add(message.Id);
                    }
                    return true;
                });

            await messageIterator.IterateAsync(cancellationToken).ConfigureAwait(false);

            // Download new mails directly with concurrency control
            if (newMailIds.Any())
            {
                _logger.Information("Starting direct download of {Count} new mails from delta sync for folder {FolderName}", newMailIds.Count, folder.FolderName);
                await DownloadMailsConcurrentlyAsync(newMailIds, folder, downloadedMessageIds, cancellationToken).ConfigureAwait(false);
            }

            // Update delta token for next sync - always store when there are no nextPageToken remaining
            if (!string.IsNullOrEmpty(messageIterator.Deltalink))
            {
                var deltaToken = GetDeltaTokenFromDeltaLink(messageIterator.Deltalink);
                await _outlookChangeProcessor.UpdateFolderDeltaSynchronizationIdentifierAsync(folder.Id, deltaToken).ConfigureAwait(false);
                folder.DeltaToken = deltaToken; // Update in-memory object too
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
                ErrorMessage = $"API error during delta sync: {apiException.Message}",
                Exception = apiException
            };

            var handled = await _errorHandlingFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
            
            if (handled)
            {
                // The error handler has processed the error (e.g., DeltaTokenExpiredHandler for 410)
                // Update in-memory folder state if it was a delta token expiration
                if (apiException.ResponseStatusCode == 410)
                {
                    folder.DeltaToken = string.Empty;
                    folder.IsInitialSyncCompleted = false;
                    _logger.Information("API error handled successfully for folder {FolderName} during delta sync. Error: {ErrorCode}", folder.FolderName, apiException.ResponseStatusCode);
                }
            }
            else
            {
                // No handler could process this error, log and re-throw
                _logger.Error(apiException, "Unhandled API error during delta sync for folder {FolderName}. Error: {ErrorCode}", folder.FolderName, apiException.ResponseStatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing delta changes for folder {FolderName}", folder.FolderName);
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

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem,
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

        Microsoft.Graph.Me.Calendars.Item.CalendarView.Delta.DeltaGetResponse eventsDeltaResponse = null;

        // TODO: Maybe we can batch each calendar?

        foreach (var calendar in localCalendars)
        {
            bool isInitialSync = string.IsNullOrEmpty(calendar.SynchronizationDeltaToken);

            if (isInitialSync)
            {
                _logger.Information("No calendar sync identifier for calendar {Name}. Performing initial sync.", calendar.Name);

                var startDate = DateTime.UtcNow.AddYears(-2).ToString("u");
                var endDate = DateTime.UtcNow.ToString("u");

                eventsDeltaResponse = await _graphClient.Me.Calendars[calendar.RemoteCalendarId].CalendarView.Delta.GetAsDeltaGetResponseAsync((requestConfiguration) =>
                {
                    requestConfiguration.QueryParameters.StartDateTime = startDate;
                    requestConfiguration.QueryParameters.EndDateTime = endDate;
                }, cancellationToken: cancellationToken);

                // No delta link. Performing initial sync.
                //eventsDeltaResponse = await _graphClient.Me.CalendarView.Delta.GetAsDeltaGetResponseAsync((requestConfiguration) =>
                //{
                //    requestConfiguration.QueryParameters.StartDateTime = startDate;
                //    requestConfiguration.QueryParameters.EndDateTime = endDate;

                //    // TODO: Expand does not work.
                //    // https://github.com/microsoftgraph/msgraph-sdk-dotnet/issues/2358

                //    requestConfiguration.QueryParameters.Expand = new string[] { "calendar($select=name,id)" }; // Expand the calendar and select name and id. Customize as needed.
                //}, cancellationToken: cancellationToken);
            }
            else
            {
                var currentDeltaToken = calendar.SynchronizationDeltaToken;

                _logger.Information("Performing delta sync for calendar {Name}.", calendar.Name);

                var requestInformation = _graphClient.Me.Calendars[calendar.RemoteCalendarId].CalendarView.Delta.ToGetRequestInformation((requestConfiguration) =>
                {

                    //requestConfiguration.QueryParameters.StartDateTime = startDate;
                    //requestConfiguration.QueryParameters.EndDateTime = endDate;
                });

                //var requestInformation = _graphClient.Me.Calendars[calendar.RemoteCalendarId].CalendarView.Delta.ToGetRequestInformation((config) =>
                //{
                //    config.QueryParameters.Top = (int)InitialMessageDownloadCountPerFolder;
                //    config.QueryParameters.Select = outlookMessageSelectParameters;
                //    config.QueryParameters.Orderby = ["receivedDateTime desc"];
                //});


                requestInformation.UrlTemplate = requestInformation.UrlTemplate.Insert(requestInformation.UrlTemplate.Length - 1, ",%24deltatoken");
                requestInformation.QueryParameters.Add("%24deltatoken", currentDeltaToken);

                eventsDeltaResponse = await _graphClient.RequestAdapter.SendAsync(requestInformation, Microsoft.Graph.Me.Calendars.Item.CalendarView.Delta.DeltaGetResponse.CreateFromDiscriminatorValue);
            }

            List<Event> events = new();

            // We must first save the parent recurring events to not lose exceptions.
            // Therefore, order the existing items by their type and save the parent recurring events first.

            var messageIteratorAsync = PageIterator<Event, Microsoft.Graph.Me.Calendars.Item.CalendarView.Delta.DeltaGetResponse>.CreatePageIterator(_graphClient, eventsDeltaResponse, (item) =>
            {
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
                try
                {
                    await _handleItemRetrievalSemaphore.WaitAsync();
                    await _outlookChangeProcessor.ManageCalendarEventAsync(item, calendar, Account).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // _logger.Error(ex, "Error occurred while handling item {Id} for calendar {Name}", item.Id, calendar.Name);
                }
                finally
                {
                    _handleItemRetrievalSemaphore.Release();
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
        }

        return default;
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

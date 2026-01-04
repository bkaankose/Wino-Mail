using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using CommunityToolkit.Mvvm.Messaging;
using Google;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Http;
using Google.Apis.PeopleService.v1;
using Google.Apis.Requests;
using Google.Apis.Services;
using MailKit;
using Microsoft.IdentityModel.Tokens;
using MimeKit;
using MoreLinq;
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
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;
using Wino.Services;
using CalendarService = Google.Apis.Calendar.v3.CalendarService;

namespace Wino.Core.Synchronizers.Mail;

[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Label))]
[JsonSerializable(typeof(Draft))]
public partial class GmailSynchronizerJsonContext : JsonSerializerContext;

/// <summary>
/// Gmail synchronizer implementation with per-folder history ID synchronization.
/// 
/// SYNCHRONIZATION STRATEGY:
/// - Uses Gmail History API for both initial and incremental sync
/// - Initial sync: Downloads top 1500 messages per folder with metadata only
/// - Incremental sync: Uses history ID to get only changes since last sync
/// - Messages are downloaded with metadata only (no MIME content during sync)
/// - MIME files are downloaded on-demand when user explicitly reads a message
/// 
/// Key implementation details:
/// - SynchronizeFolderAsync: Main entry point for per-folder synchronization
/// - DownloadMessagesForFolderAsync: Downloads top 1500 messages for initial sync
/// - SynchronizeDeltaAsync: Processes incremental changes using history ID
/// - CreateMinimalMailCopyAsync: Extracts MailCopy fields from Gmail Metadata format
/// - DownloadMissingMimeMessageAsync: Downloads raw MIME only when explicitly requested
/// </summary>
public class GmailSynchronizer : WinoSynchronizer<IClientServiceRequest, Message, Event>, IHttpClientFactory
{
    public override uint BatchModificationSize => 1000;

    /// <summary>
    /// Maximum messages to fetch per folder during initial sync (1500).
    /// All messages are downloaded with METADATA ONLY - no raw MIME content.
    /// Uses Gmail API's Metadata format which includes headers, labels, and snippet but NOT full message body.
    /// </summary>
    public override uint InitialMessageDownloadCountPerFolder => 1500;

    // It's actually 100. But Gmail SDK has internal bug for Out of Memory exception.
    // https://github.com/googleapis/google-api-dotnet-client/issues/2603
    private const uint MaximumAllowedBatchRequestSize = 10;

    private readonly ConfigurableHttpClient _googleHttpClient;
    private readonly GmailService _gmailService;
    private readonly CalendarService _calendarService;
    private readonly PeopleServiceService _peopleService;

    private readonly IGmailChangeProcessor _gmailChangeProcessor;
    private readonly IGmailSynchronizerErrorHandlerFactory _gmailSynchronizerErrorHandlerFactory;
    private readonly ILogger _logger = Log.ForContext<GmailSynchronizer>();

    // Keeping a reference for quick access to the virtual archive folder.
    private Guid? archiveFolderId;

    public GmailSynchronizer(MailAccount account,
                             IGmailAuthenticator authenticator,
                             IGmailChangeProcessor gmailChangeProcessor,
                             IGmailSynchronizerErrorHandlerFactory gmailSynchronizerErrorHandlerFactory) : base(account, WeakReferenceMessenger.Default)
    {
        var messageHandler = new GmailClientMessageHandler(authenticator, account);

        var initializer = new BaseClientService.Initializer()
        {
            HttpClientFactory = this
        };

        _googleHttpClient = new ConfigurableHttpClient(messageHandler);
        _gmailService = new GmailService(initializer);
        _peopleService = new PeopleServiceService(initializer);
        _calendarService = new CalendarService(initializer);

        _gmailChangeProcessor = gmailChangeProcessor;
        _gmailSynchronizerErrorHandlerFactory = gmailSynchronizerErrorHandlerFactory;
    }

    public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => _googleHttpClient;

    public override async Task<ProfileInformation> GetProfileInformationAsync()
    {
        var profileRequest = _peopleService.People.Get("people/me");
        profileRequest.PersonFields = "names,photos,emailAddresses";

        string senderName = string.Empty, base64ProfilePicture = string.Empty, address = string.Empty;

        var userProfile = await profileRequest.ExecuteAsync();

        senderName = userProfile.Names?.FirstOrDefault()?.DisplayName ?? Account.SenderName;

        var profilePicture = userProfile.Photos?.FirstOrDefault()?.Url ?? string.Empty;

        if (!string.IsNullOrEmpty(profilePicture))
        {
            base64ProfilePicture = await GetProfilePictureBase64EncodedAsync(profilePicture).ConfigureAwait(false);
        }

        address = userProfile.EmailAddresses.FirstOrDefault(a => a.Metadata.Primary == true).Value;

        return new ProfileInformation(senderName, base64ProfilePicture, address);
    }

    protected override async Task SynchronizeAliasesAsync()
    {
        var sendAsListRequest = _gmailService.Users.Settings.SendAs.List("me");
        var sendAsListResponse = await sendAsListRequest.ExecuteAsync();
        var remoteAliases = sendAsListResponse.GetRemoteAliases();

        await _gmailChangeProcessor.UpdateRemoteAliasInformationAsync(Account, remoteAliases).ConfigureAwait(false);
    }

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        _logger.Information("Internal mail synchronization started for {Name}", Account.Name);

        // Make sure that virtual archive folder exists before all.
        if (!archiveFolderId.HasValue)
            await InitializeArchiveFolderAsync().ConfigureAwait(false);

        // Gmail must always synchronize folders before because it doesn't have a per-folder sync.
        bool shouldSynchronizeFolders = true;

        if (shouldSynchronizeFolders)
        {
            _logger.Information("Synchronizing folders for {Name}", Account.Name);
            UpdateSyncProgress(0, 0, "Synchronizing folders...");

            try
            {
                await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException googleException) when (googleException.Message.Contains("Mail service not enabled"))
            {
                throw new GmailServiceDisabledException();
            }
            catch (Exception)
            {
                throw;
            }

            _logger.Information("Synchronizing folders for {Name} is completed", Account.Name);
            UpdateSyncProgress(0, 0, "Folders synchronized");
        }

        // There is no specific folder synchronization in Gmail.
        // Therefore we need to stop the synchronization at this point
        // if type is only folder metadata sync.

        if (options.Type == MailSynchronizationType.FoldersOnly) return MailSynchronizationResult.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        bool isInitialSync = string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier);

        _logger.Debug("Is initial synchronization: {IsInitialSync}", isInitialSync);

        var downloadedMessageIds = new List<string>();

        // Get all folders to synchronize
        var synchronizationFolders = await _gmailChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);

        _logger.Information("Synchronizing {Count} folders for {Name}", synchronizationFolders.Count, Account.Name);

        var totalFolders = synchronizationFolders.Count;

        for (int i = 0; i < totalFolders; i++)
        {
            var folder = synchronizationFolders[i];

            // Update progress based on folder completion
            UpdateSyncProgress(totalFolders, totalFolders - (i + 1), $"Syncing {folder.FolderName}...");

            var folderDownloadedMessageIds = await SynchronizeFolderAsync(folder, cancellationToken).ConfigureAwait(false);
            downloadedMessageIds.AddRange(folderDownloadedMessageIds);
        }

        // Process incremental changes using history API if we have a history ID
        if (!string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier))
        {
            UpdateSyncProgress(0, 0, "Synchronizing changes...");
            await SynchronizeDeltaAsync(options, cancellationToken).ConfigureAwait(false);
            UpdateSyncProgress(0, 0, "Changes synchronized");
        }

        // Get all unread new downloaded items for notifications
        var unreadNewItems = await _gmailChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);

        return MailSynchronizationResult.Completed(unreadNewItems);
    }

    /// <summary>
    /// Synchronizes a single folder by downloading top 1500 messages with metadata only.
    /// </summary>
    private async Task<List<string>> SynchronizeFolderAsync(MailItemFolder folder, CancellationToken cancellationToken)
    {
        var downloadedMessageIds = new List<string>();

        cancellationToken.ThrowIfCancellationRequested();

        _logger.Debug("Synchronizing folder {FolderName} (label: {LabelId})", folder.FolderName, folder.RemoteFolderId);

        try
        {
            // Download top 1500 messages for this folder
            await DownloadMessagesForFolderAsync(folder, downloadedMessageIds, cancellationToken).ConfigureAwait(false);

            if (downloadedMessageIds.Any())
            {
                _logger.Information("Downloaded {Count} messages for folder {FolderName}", downloadedMessageIds.Count, folder.FolderName);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error synchronizing folder {FolderName}", folder.FolderName);
            throw;
        }

        return downloadedMessageIds;
    }

    /// <summary>
    /// Downloads top 1500 messages for a folder using Gmail API with metadata only.
    /// </summary>
    private async Task DownloadMessagesForFolderAsync(MailItemFolder folder, List<string> downloadedMessageIds, CancellationToken cancellationToken)
    {
        _logger.Debug("Downloading messages for folder {FolderName}", folder.FolderName);

        try
        {
            var totalDownloaded = 0;
            string pageToken = null;

            // Gmail API returns messages newest first by default
            // We'll download up to 1500 messages per folder
            var remainingToDownload = (int)InitialMessageDownloadCountPerFolder;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = _gmailService.Users.Messages.List("me");
                request.LabelIds = new Google.Apis.Util.Repeatable<string>(new[] { folder.RemoteFolderId });
                request.MaxResults = Math.Min(remainingToDownload, 500); // API max is 500
                request.PageToken = pageToken;

                var response = await request.ExecuteAsync(cancellationToken);

                if (response.Messages != null && response.Messages.Count > 0)
                {
                    var messageIds = response.Messages.Select(m => m.Id).ToList();

                    // Download metadata in batches
                    await DownloadMessagesInBatchAsync(messageIds, downloadRawMime: false, cancellationToken).ConfigureAwait(false);

                    downloadedMessageIds.AddRange(messageIds);
                    totalDownloaded += messageIds.Count;
                    remainingToDownload -= messageIds.Count;

                    _logger.Debug("Downloaded {Count} messages for folder {FolderName} (total: {Total})", messageIds.Count, folder.FolderName, totalDownloaded);

                    // Update progress
                    UpdateSyncProgress(0, 0, $"Downloaded {totalDownloaded} messages from {folder.FolderName}");
                }

                pageToken = response.NextPageToken;

                // Stop if we've downloaded enough messages or no more pages
                if (remainingToDownload <= 0 || string.IsNullOrEmpty(pageToken))
                    break;

            } while (!string.IsNullOrEmpty(pageToken));

            // Store history ID for future incremental syncs
            var profile = await _gmailService.Users.GetProfile("me").ExecuteAsync(cancellationToken);
            Account.SynchronizationDeltaIdentifier = profile.HistoryId.ToString();
            await _gmailChangeProcessor.UpdateAccountAsync(Account).ConfigureAwait(false);

            _logger.Information("Completed downloading {Count} messages for folder {FolderName}", totalDownloaded, folder.FolderName);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.Warning("Rate limit exceeded while downloading messages for folder {FolderName}. Retrying after delay.", folder.FolderName);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error downloading messages for folder {FolderName}", folder.FolderName);
            throw;
        }
    }

    private async Task SynchronizeDeltaAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var historyRequest = _gmailService.Users.History.List("me");
            historyRequest.StartHistoryId = ulong.Parse(Account.SynchronizationDeltaIdentifier!);

            var historyResponse = await historyRequest.ExecuteAsync();

            if (historyResponse.History != null)
            {
                var addedMessageIds = new List<string>();

                // Collect all added messages first
                foreach (var historyRecord in historyResponse.History)
                {
                    if (historyRecord.MessagesAdded != null)
                    {
                        addedMessageIds.AddRange(historyRecord.MessagesAdded.Select(ma => ma.Message.Id));
                    }
                }

                // Process added messages in batches if any
                // During delta sync, download with Raw format to get MIME content
                if (addedMessageIds.Count != 0)
                {
                    await DownloadMessagesInBatchAsync(addedMessageIds, downloadRawMime: true, cancellationToken).ConfigureAwait(false);
                }

                // Process other history changes
                foreach (var historyRecord in historyResponse.History)
                {
                    await ProcessHistoryChangesAsync(historyResponse).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {

            throw;
        }
    }

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        _logger.Information("Internal calendar synchronization started for {Name}", Account.Name);

        cancellationToken.ThrowIfCancellationRequested();

        await SynchronizeCalendarsAsync(cancellationToken).ConfigureAwait(false);

        bool isInitialSync = string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier);

        _logger.Debug("Is initial synchronization: {IsInitialSync}", isInitialSync);

        var localCalendars = await _gmailChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        // TODO: Better logging and exception handling.
        foreach (var calendar in localCalendars)
        {
            var request = _calendarService.Events.List(calendar.RemoteCalendarId);

            request.SingleEvents = false;
            request.ShowDeleted = true;

            if (!string.IsNullOrEmpty(calendar.SynchronizationDeltaToken))
            {
                // If a sync token is available, perform an incremental sync
                request.SyncToken = calendar.SynchronizationDeltaToken;
            }
            else
            {
                // If no sync token, perform an initial sync
                // Fetch events from the past year

                request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow.AddYears(-1);
            }

            string nextPageToken;
            string syncToken;

            var allEvents = new List<Event>();

            do
            {
                // Execute the request
                var events = await request.ExecuteAsync();

                // Process the fetched events
                if (events.Items != null)
                {
                    allEvents.AddRange(events.Items);
                }

                // Get the next page token and sync token
                nextPageToken = events.NextPageToken;
                syncToken = events.NextSyncToken;

                // Set the next page token for subsequent requests
                request.PageToken = nextPageToken;

            } while (!string.IsNullOrEmpty(nextPageToken));

            calendar.SynchronizationDeltaToken = syncToken;

            // allEvents contains new or updated events.
            // Process them and create/update local calendar items.

            foreach (var @event in allEvents)
            {
                // TODO: Exception handling for event processing.
                // TODO: Also update attendees and other properties.

                await _gmailChangeProcessor.ManageCalendarEventAsync(@event, calendar, Account).ConfigureAwait(false);
            }

            await _gmailChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        return default;
    }

    private async Task SynchronizeCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var calendarListRequest = _calendarService.CalendarList.List();
        var calendarListResponse = await calendarListRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (calendarListResponse.Items == null)
        {
            _logger.Warning("No calendars found for {Name}", Account.Name);
            return;
        }

        var localCalendars = await _gmailChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        List<AccountCalendar> insertedCalendars = new();
        List<AccountCalendar> updatedCalendars = new();
        List<AccountCalendar> deletedCalendars = new();

        // 1. Handle deleted calendars.

        foreach (var calendar in localCalendars)
        {
            var remoteCalendar = calendarListResponse.Items.FirstOrDefault(a => a.Id == calendar.RemoteCalendarId);
            if (remoteCalendar == null)
            {
                // Local calendar doesn't exists remotely. Delete local copy.

                await _gmailChangeProcessor.DeleteAccountCalendarAsync(calendar).ConfigureAwait(false);
                deletedCalendars.Add(calendar);
            }
        }

        // Delete the deleted folders from local list.
        deletedCalendars.ForEach(a => localCalendars.Remove(a));

        // 2. Handle update/insert based on remote calendars.
        foreach (var calendar in calendarListResponse.Items)
        {
            var existingLocalCalendar = localCalendars.FirstOrDefault(a => a.RemoteCalendarId == calendar.Id);
            if (existingLocalCalendar == null)
            {
                // Insert new calendar.
                var localCalendar = calendar.AsCalendar(Account.Id);
                insertedCalendars.Add(localCalendar);
            }
            else
            {
                // Update existing calendar. Right now we only update the name.
                if (ShouldUpdateCalendar(calendar, existingLocalCalendar))
                {
                    existingLocalCalendar.Name = calendar.Summary;

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
            await _gmailChangeProcessor.InsertAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        foreach (var calendar in updatedCalendars)
        {
            await _gmailChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        if (insertedCalendars.Any() || deletedCalendars.Any() || updatedCalendars.Any())
        {
            // TODO: Notify calendar updates.
            // WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
        }
    }

    private async Task InitializeArchiveFolderAsync()
    {
        var localFolders = await _gmailChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);

        // Handling of Gmail special virtual Archive folder.
        // We will generate a new virtual folder if doesn't exist.

        if (!localFolders.Any(a => a.SpecialFolderType == SpecialFolderType.Archive && a.RemoteFolderId == ServiceConstants.ARCHIVE_LABEL_ID))
        {
            archiveFolderId = Guid.NewGuid();

            var archiveFolder = new MailItemFolder()
            {
                FolderName = "Archive", // will be localized. N/A
                RemoteFolderId = ServiceConstants.ARCHIVE_LABEL_ID,
                Id = archiveFolderId.Value,
                MailAccountId = Account.Id,
                SpecialFolderType = SpecialFolderType.Archive,
                IsSynchronizationEnabled = true,
                IsSystemFolder = true,
                IsSticky = true,
                IsHidden = false,
                ShowUnreadCount = true
            };

            await _gmailChangeProcessor.InsertFolderAsync(archiveFolder).ConfigureAwait(false);

            // Migration-> User might've already have another special folder for Archive.
            // We must remove that type assignment.
            // This code can be removed after sometime.

            var otherArchiveFolders = localFolders.Where(a => a.SpecialFolderType == SpecialFolderType.Archive && a.Id != archiveFolderId.Value).ToList();

            foreach (var otherArchiveFolder in otherArchiveFolders)
            {
                otherArchiveFolder.SpecialFolderType = SpecialFolderType.Other;
                await _gmailChangeProcessor.UpdateFolderAsync(otherArchiveFolder).ConfigureAwait(false);
            }
        }
        else
        {
            archiveFolderId = localFolders.First(a => a.SpecialFolderType == SpecialFolderType.Archive && a.RemoteFolderId == ServiceConstants.ARCHIVE_LABEL_ID).Id;
        }
    }

    private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken = default)
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

            // Gmail's Archive folder is virtual older for Wino. Skip it.
            if (localFolder.SpecialFolderType == SpecialFolderType.Archive) continue;

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
                    existingLocalFolder.TextColorHex = remoteFolder.Color?.TextColor;
                    existingLocalFolder.BackgroundColorHex = remoteFolder.Color?.BackgroundColor;

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

        if (insertedFolders.Any() || deletedFolders.Any() || updatedFolders.Any())
        {
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
        }
    }

    private bool ShouldUpdateCalendar(CalendarListEntry calendarListEntry, AccountCalendar accountCalendar)
    {
        // TODO: Only calendar name is updated for now. We can add more checks here.

        var remoteCalendarName = calendarListEntry.Summary;
        var localCalendarName = accountCalendar.Name;

        return !localCalendarName.Equals(remoteCalendarName, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldUpdateFolder(Label remoteFolder, MailItemFolder existingLocalFolder)
    {
        var remoteFolderName = GoogleIntegratorExtensions.GetFolderName(remoteFolder.Name);
        var localFolderName = GoogleIntegratorExtensions.GetFolderName(existingLocalFolder.FolderName);

        bool isNameChanged = !localFolderName.Equals(remoteFolderName, StringComparison.OrdinalIgnoreCase);
        bool isColorChanged = existingLocalFolder.BackgroundColorHex != remoteFolder.Color?.BackgroundColor ||
                existingLocalFolder.TextColorHex != remoteFolder.Color?.TextColor;

        return isNameChanged || isColorChanged;
    }

    /// <summary>
    /// Returns a single get request to retrieve the message with the given id.
    /// Always uses Metadata format to download only headers and labels - NOT raw MIME content.
    /// MIME content is only downloaded when explicitly needed via DownloadMissingMimeMessageAsync.
    /// </summary>
    /// <param name="messageId">Message to download.</param>
    /// <returns>Get request for message with Metadata format.</returns>
    private UsersResource.MessagesResource.GetRequest CreateSingleMessageGet(string messageId)
    {
        var singleRequest = _gmailService.Users.Messages.Get("me", messageId);

        // Always use Metadata format for synchronization - this populates Payload.Headers
        // but does NOT download the raw MIME content, saving significant bandwidth and time
        singleRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;

        return singleRequest;
    }

    /// <summary>
    /// Returns a single get request to retrieve the message with Raw format (includes MIME).
    /// Used during delta sync to download full message content.
    /// </summary>
    /// <param name="messageId">Message to download.</param>
    /// <returns>Get request for message with Raw format.</returns>
    private UsersResource.MessagesResource.GetRequest CreateSingleMessageGetRaw(string messageId)
    {
        var singleRequest = _gmailService.Users.Messages.Get("me", messageId);

        // Use Raw format to get full MIME content
        singleRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;

        return singleRequest;
    }

    /// <summary>
    /// Processes the delta changes for the given history changes.
    /// Message downloads are not handled here since it's better to batch them.
    /// </summary>
    /// <param name="listHistoryResponse">List of history changes.</param>
    private async Task ProcessHistoryChangesAsync(ListHistoryResponse listHistoryResponse)
    {
        _logger.Debug("Processing delta change {HistoryId} for {Name}", listHistoryResponse.HistoryId.GetValueOrDefault(), Account.Name);

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

    private async Task HandleArchiveAssignmentAsync(string archivedMessageId)
    {
        // Ignore if the message is already in the archive.
        bool archived = await _gmailChangeProcessor.IsMailExistsInFolderAsync(archivedMessageId, archiveFolderId.Value);

        if (archived) return;

        _logger.Debug("Processing archive assignment for message {Id}", archivedMessageId);

        await _gmailChangeProcessor.CreateAssignmentAsync(Account.Id, archivedMessageId, ServiceConstants.ARCHIVE_LABEL_ID).ConfigureAwait(false);
    }

    private async Task HandleUnarchiveAssignmentAsync(string unarchivedMessageId)
    {
        // Ignore if the message is not in the archive.
        bool archived = await _gmailChangeProcessor.IsMailExistsInFolderAsync(unarchivedMessageId, archiveFolderId.Value);
        if (!archived) return;

        _logger.Debug("Processing un-archive assignment for message {Id}", unarchivedMessageId);

        await _gmailChangeProcessor.DeleteAssignmentAsync(Account.Id, unarchivedMessageId, ServiceConstants.ARCHIVE_LABEL_ID).ConfigureAwait(false);
    }

    private async Task HandleLabelAssignmentAsync(HistoryLabelAdded addedLabel)
    {
        var messageId = addedLabel.Message.Id;

        _logger.Debug("Processing label assignment for message {MessageId}", messageId);

        foreach (var labelId in addedLabel.LabelIds)
        {
            // When UNREAD label is added mark the message as un-read.
            if (labelId == ServiceConstants.UNREAD_LABEL_ID)
                await _gmailChangeProcessor.ChangeMailReadStatusAsync(messageId, false).ConfigureAwait(false);

            // When STARRED label is added mark the message as flagged.
            if (labelId == ServiceConstants.STARRED_LABEL_ID)
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
            if (labelId == ServiceConstants.UNREAD_LABEL_ID)
                await _gmailChangeProcessor.ChangeMailReadStatusAsync(messageId, true).ConfigureAwait(false);

            // When STARRED label is removed mark the message as un-flagged.
            if (labelId == ServiceConstants.STARRED_LABEL_ID)
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

    public override List<IRequestBundle<IClientServiceRequest>> Move(BatchMoveRequest request)
    {
        var toFolder = request[0].ToFolder;
        var fromFolder = request[0].FromFolder;

        // Sent label can't be removed from mails for Gmail.
        // They are automatically assigned by Gmail.
        // When you delete sent mail from gmail web portal, it's moved to Trash
        // but still has Sent label. It's just hidden from the user.
        // Proper assignments will be done later on CreateAssignment call to mimic this behavior.

        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
            AddLabelIds = [toFolder.RemoteFolderId]
        };

        // Archived item is being moved to different folder.
        // Unarchive will move it to Inbox, so this is a different case.
        // We can't remove ARCHIVE label because it's a virtual folder and does not exist in Gmail.
        // We will just add the target label and Gmail will handle the rest.

        if (fromFolder.SpecialFolderType == SpecialFolderType.Archive)
        {
            batchModifyRequest.AddLabelIds = [toFolder.RemoteFolderId];
        }
        else if (fromFolder.SpecialFolderType != SpecialFolderType.Sent)
        {
            // Only add remove label ids if the source folder is not sent folder.
            batchModifyRequest.RemoveLabelIds = [fromFolder.RemoteFolderId];
        }

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> ChangeFlag(BatchChangeFlagRequest request)
    {
        bool isFlagged = request[0].IsFlagged;

        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
        };

        if (isFlagged)
            batchModifyRequest.AddLabelIds = new List<string>() { ServiceConstants.STARRED_LABEL_ID };
        else
            batchModifyRequest.RemoveLabelIds = new List<string>() { ServiceConstants.STARRED_LABEL_ID };

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> MarkRead(BatchMarkReadRequest request)
    {
        bool readStatus = request[0].IsRead;

        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
        };

        if (readStatus)
            batchModifyRequest.RemoveLabelIds = new List<string>() { ServiceConstants.UNREAD_LABEL_ID };
        else
            batchModifyRequest.AddLabelIds = new List<string>() { ServiceConstants.UNREAD_LABEL_ID };

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> Delete(BatchDeleteRequest request)
    {
        var batchModifyRequest = new BatchDeleteMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
        };

        var networkCall = _gmailService.Users.Messages.BatchDelete(batchModifyRequest, "me");

        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> CreateDraft(CreateDraftRequest singleRequest)
    {
        Draft draft = null;

        // It's new mail. Not a reply
        if (singleRequest.DraftPreperationRequest.ReferenceMailCopy == null)
            draft = PrepareGmailDraft(singleRequest.DraftPreperationRequest.CreatedLocalDraftMimeMessage);
        else
            draft = PrepareGmailDraft(singleRequest.DraftPreperationRequest.CreatedLocalDraftMimeMessage,
                singleRequest.DraftPreperationRequest.ReferenceMailCopy.ThreadId,
                singleRequest.DraftPreperationRequest.ReferenceMailCopy.DraftId);

        var networkCall = _gmailService.Users.Drafts.Create(draft, "me");

        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, singleRequest, singleRequest)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> Archive(BatchArchiveRequest request)
    {
        bool isArchiving = request[0].IsArchiving;
        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList()
        };

        if (isArchiving)
        {
            batchModifyRequest.RemoveLabelIds = new[] { ServiceConstants.INBOX_LABEL_ID };
        }
        else
        {
            batchModifyRequest.AddLabelIds = new[] { ServiceConstants.INBOX_LABEL_ID };
        }

        var networkCall = _gmailService.Users.Messages.BatchModify(batchModifyRequest, "me");

        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> SendDraft(SendDraftRequest singleDraftRequest)
    {

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

        var networkCall = _gmailService.Users.Drafts.Send(draft, "me");

        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, singleDraftRequest, singleDraftRequest)];
    }

    public override async Task<List<MailCopy>> OnlineSearchAsync(string queryText, List<IMailItemFolder> folders, CancellationToken cancellationToken = default)
    {
        var request = _gmailService.Users.Messages.List("me");
        request.Q = queryText;
        request.MaxResults = 500; // Max 500 is returned.

        string pageToken = null;

        List<Message> messagesToDownload = [];

        do
        {
            if (queryText.StartsWith("label:") || queryText.StartsWith("in:"))
            {
                // Ignore the folders if the query starts with these keywords.
                // User is trying to list everything.
            }
            else if (folders?.Count > 0)
            {
                request.LabelIds = folders.Select(a => a.RemoteFolderId).ToList();
            }

            if (!string.IsNullOrEmpty(pageToken))
            {
                request.PageToken = pageToken;
            }

            var response = await request.ExecuteAsync(cancellationToken);
            if (response.Messages == null) break;

            // Handle skipping manually
            messagesToDownload.AddRange(response.Messages);

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        // Do not download messages that exists, but return them for listing.

        var messageIds = messagesToDownload.Select(a => a.Id);

        var downloadRequireMessageIds = messageIds.Except(await _gmailChangeProcessor.AreMailsExistsAsync(messageIds));

        // Download missing messages in batch.
        await DownloadMessagesInBatchAsync(downloadRequireMessageIds, cancellationToken).ConfigureAwait(false);

        // Get results from database and return.

        return await _gmailChangeProcessor.GetMailCopiesAsync(messageIds);
    }

    /// <summary>
    /// Downloads multiple messages in batches with metadata only (no MIME) and creates mail packages.
    /// Uses Gmail batch API to download up to MaximumAllowedBatchRequestSize messages per request.
    /// Used for initial sync where MIME is not needed.
    /// </summary>
    /// <param name="messageIds">List of Gmail message IDs to download</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task DownloadMessagesInBatchAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
    {
        await DownloadMessagesInBatchAsync(messageIds, downloadRawMime: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads multiple messages in batches with optional MIME content and creates mail packages.
    /// Uses Gmail batch API to download up to MaximumAllowedBatchRequestSize messages per request.
    /// </summary>
    /// <param name="messageIds">List of Gmail message IDs to download</param>
    /// <param name="downloadRawMime">True to download Raw format with MIME, false for Metadata only</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task DownloadMessagesInBatchAsync(IEnumerable<string> messageIds, bool downloadRawMime, CancellationToken cancellationToken = default)
    {
        var messageIdList = messageIds.ToList();
        if (messageIdList.Count == 0) return;

        // Split into batches based on MaximumAllowedBatchRequestSize
        var batches = messageIdList.Batch((int)MaximumAllowedBatchRequestSize);

        foreach (var batch in batches)
        {
            var batchRequest = new BatchRequest(_gmailService);
            var downloadedMessages = new List<Message>();
            var batchTasks = new List<Task>();

            foreach (var messageId in batch)
            {
                var request = downloadRawMime ? CreateSingleMessageGetRaw(messageId) : CreateSingleMessageGet(messageId);

                batchRequest.Queue<Message>(request, (message, error, index, httpMessage) =>
                {
                    var task = Task.Run(async () =>
                    {
                        if (error != null)
                        {
                            _logger.Warning("Failed to download message {MessageId}: {Error}", messageId, error.Message);
                            return;
                        }

                        if (message != null)
                        {
                            lock (downloadedMessages)
                            {
                                downloadedMessages.Add(message);
                            }
                        }
                    });

                    batchTasks.Add(task);
                });
            }

            // Execute the batch request
            await batchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(batchTasks).ConfigureAwait(false);

            // Process all downloaded messages
            foreach (var gmailMessage in downloadedMessages)
            {
                try
                {
                    MimeMessage mimeMessage = null;

                    // Extract MIME if we downloaded raw format
                    if (downloadRawMime)
                    {
                        mimeMessage = gmailMessage.GetGmailMimeMessage();

                        if (mimeMessage == null)
                        {
                            _logger.Warning("Failed to parse MIME for message {MessageId}", gmailMessage.Id);
                        }
                    }

                    // Create mail packages from metadata (or raw if downloaded)
                    var packages = await CreateNewMailPackagesAsync(gmailMessage, null, cancellationToken).ConfigureAwait(false);

                    if (packages != null)
                    {
                        // For Gmail, multiple packages can share the same message (different labels/folders)
                        // They should all share the same FileId so MIME is stored only once
                        Guid sharedFileId = Guid.NewGuid();

                        foreach (var package in packages)
                        {
                            // Set the same FileId for all copies
                            package.Copy.FileId = sharedFileId;

                            // Create the mail copy with the MIME (if downloaded)
                            var packageWithMime = downloadRawMime && mimeMessage != null
                                ? new NewMailItemPackage(package.Copy, mimeMessage, package.AssignedRemoteFolderId)
                                : package;

                            await _gmailChangeProcessor.CreateMailAsync(Account.Id, packageWithMime).ConfigureAwait(false);
                        }
                    }

                    // Update sync identifier if available
                    if (gmailMessage.HistoryId.HasValue)
                    {
                        await UpdateAccountSyncIdentifierAsync(gmailMessage.HistoryId.Value).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to process downloaded message {MessageId}", gmailMessage.Id);
                }
            }
        }
    }

    /// <summary>
    /// Downloads a single message by ID with metadata only (no MIME) and creates mail packages.
    /// </summary>
    /// <param name="messageId">Gmail message ID to download</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task DownloadSingleMessageMetadataAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var request = CreateSingleMessageGet(messageId);
        var gmailMessage = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (gmailMessage == null)
        {
            _logger.Warning("Failed to download message metadata for {MessageId}", messageId);
            return;
        }

        // Create mail packages from metadata
        var packages = await CreateNewMailPackagesAsync(gmailMessage, null, cancellationToken).ConfigureAwait(false);

        if (packages != null)
        {
            foreach (var package in packages)
            {
                await _gmailChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);
            }
        }

        // Update sync identifier if available
        if (gmailMessage.HistoryId.HasValue)
        {
            await UpdateAccountSyncIdentifierAsync(gmailMessage.HistoryId.Value).ConfigureAwait(false);
        }
    }

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem,
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

    public override async Task DownloadCalendarAttachmentAsync(
        Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem,
        Wino.Core.Domain.Entities.Calendar.CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Gmail calendar attachments are stored in Google Drive
            // RemoteAttachmentId contains either FileId or FileUrl
            // For simplicity, we'll try to download from the FileId/FileUrl
            
            if (string.IsNullOrEmpty(attachment.RemoteAttachmentId))
            {
                _logger.Error("RemoteAttachmentId is empty for attachment {AttachmentId}", attachment.Id);
                throw new InvalidOperationException("RemoteAttachmentId is required to download Gmail calendar attachment.");
            }

            // Gmail calendar attachments are links to Google Drive files
            // The attachment.RemoteAttachmentId is either a FileId or FileUrl
            // Since we can't directly download from Calendar API, this would require Drive API access
            // For now, throw NotSupportedException as Gmail attachments require additional Drive API setup
            
            _logger.Warning("Gmail calendar attachment download requires Google Drive API access. FileId/URL: {RemoteId}", attachment.RemoteAttachmentId);
            throw new NotSupportedException("Gmail calendar attachments are stored in Google Drive and require additional API configuration to download.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error downloading Gmail calendar attachment {AttachmentId}", attachment.Id);
            throw;
        }
    }

    public override List<IRequestBundle<IClientServiceRequest>> RenameFolder(RenameFolderRequest request)
    {
        var label = new Label()
        {
            Name = request.NewFolderName
        };

        var networkCall = _gmailService.Users.Labels.Update(label, "me", request.Folder.RemoteFolderId);

        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> EmptyFolder(EmptyFolderRequest request)
    {
        // Create batch delete request.

        var deleteRequests = request.MailsToDelete.Select(a => new DeleteRequest(a));

        return Delete(new BatchDeleteRequest(deleteRequests));
    }

    public override List<IRequestBundle<IClientServiceRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    #endregion

    #region Request Execution

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<IClientServiceRequest>> batchedRequests,
                                                          CancellationToken cancellationToken = default)
    {
        var batchedBundles = batchedRequests.Batch((int)MaximumAllowedBatchRequestSize);
        var bundleCount = batchedBundles.Count();

        for (int i = 0; i < bundleCount; i++)
        {
            var bundle = batchedBundles.ElementAt(i);

            var nativeBatchRequest = new BatchRequest(_gmailService);

            var bundleRequestCount = bundle.Count();

            var bundleTasks = new List<Task>();

            for (int k = 0; k < bundleRequestCount; k++)
            {
                var requestBundle = bundle.ElementAt(k);
                requestBundle.UIChangeRequest?.ApplyUIChanges();

                nativeBatchRequest.Queue<object>(requestBundle.NativeRequest, (content, error, index, message)
                    => bundleTasks.Add(ProcessSingleNativeRequestResponseAsync(requestBundle, error, message, cancellationToken)));
            }

            await nativeBatchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            await Task.WhenAll(bundleTasks);
        }
    }

    private async Task ProcessGmailRequestErrorAsync(RequestError error, IRequestBundle<IClientServiceRequest> bundle)
    {
        if (error == null) return;

        // Create error context
        var errorContext = new SynchronizerErrorContext
        {
            ErrorCode = error.Code,
            ErrorMessage = error.Message,
            RequestBundle = bundle,
            AdditionalData = new Dictionary<string, object>
            {
                { "Account", Account },
                { "Error", error }
            }
        };

        // Try to handle the error with registered handlers
        var handled = await _gmailSynchronizerErrorHandlerFactory.HandleErrorAsync(errorContext);

        // If not handled by any specific handler, apply default error handling
        if (!handled)
        {
            // OutOfMemoryException is a known bug in Gmail SDK.
            if (error.Code == 0)
            {
                bundle?.UIChangeRequest?.RevertUIChanges();
                throw new OutOfMemoryException(error.Message);
            }

            // Entity not found.
            if (error.Code == 404)
            {
                bundle?.UIChangeRequest?.RevertUIChanges();
                throw new SynchronizerEntityNotFoundException(error.Message);
            }

            if (!string.IsNullOrEmpty(error.Message))
            {
                bundle?.UIChangeRequest?.RevertUIChanges();
                error.Errors?.ForEach(error => _logger.Error("Unknown Gmail SDK error for {Name}\n{Error}", Account.Name, error));

                throw new SynchronizerException(error.Message);
            }
        }
    }

    private bool ShouldUpdateSyncIdentifier(ulong? historyId)
    {
        if (historyId == null) return false;

        var newHistoryId = historyId.Value;

        return Account.SynchronizationDeltaIdentifier == null ||
            (ulong.TryParse(Account.SynchronizationDeltaIdentifier, out ulong currentIdentifier) && newHistoryId > currentIdentifier);
    }

    private async Task UpdateAccountSyncIdentifierAsync(ulong? historyId)
    {
        if (ShouldUpdateSyncIdentifier(historyId))
        {
            Account.SynchronizationDeltaIdentifier = await _gmailChangeProcessor.UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, historyId.Value.ToString());
        }
    }

    private async Task ProcessSingleNativeRequestResponseAsync(IRequestBundle<IClientServiceRequest> bundle,
                                                               RequestError error,
                                                               HttpResponseMessage httpResponseMessage,
                                                               CancellationToken cancellationToken = default)
    {
        await ProcessGmailRequestErrorAsync(error, bundle);

        if (bundle is HttpRequestBundle<IClientServiceRequest, Message> messageBundle)
        {
            var gmailMessage = await messageBundle.DeserializeBundleAsync(httpResponseMessage, GmailSynchronizerJsonContext.Default.Message, cancellationToken).ConfigureAwait(false);

            if (gmailMessage == null) return;

            // Create mail packages from the downloaded message
            var packages = await CreateNewMailPackagesAsync(gmailMessage, null, cancellationToken).ConfigureAwait(false);

            if (packages != null)
            {
                foreach (var package in packages)
                {
                    await _gmailChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);
                }
            }

            await UpdateAccountSyncIdentifierAsync(gmailMessage.HistoryId).ConfigureAwait(false);
        }
        else if (bundle is HttpRequestBundle<IClientServiceRequest, Label> folderBundle)
        {
            // TODO: Handle new Gmail Label added or updated.
        }
        else if (bundle is HttpRequestBundle<IClientServiceRequest, Draft> draftBundle && draftBundle.Request is CreateDraftRequest createDraftRequest)
        {
            // New draft mail is created.

            var messageDraft = await draftBundle.DeserializeBundleAsync(httpResponseMessage, GmailSynchronizerJsonContext.Default.Draft, cancellationToken).ConfigureAwait(false);

            if (messageDraft == null) return;

            var localDraftCopy = createDraftRequest.DraftPreperationRequest.CreatedLocalDraftCopy;

            // Here we have DraftId, MessageId and ThreadId.
            // Update the local copy properties and re-synchronize to get the original message and update history.

            // We don't fetch the single message here because it may skip some of the history changes when the
            // fetch updates the historyId. Therefore we need to re-synchronize to get the latest history changes
            // which will have the original message downloaded eventually.

            await _gmailChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopy.UniqueId, messageDraft.Message.Id, messageDraft.Id, messageDraft.Message.ThreadId);

            var options = new MailSynchronizationOptions()
            {
                AccountId = Account.Id,
                Type = MailSynchronizationType.FullFolders
            };

            await SynchronizeMailsInternalAsync(options, cancellationToken);
        }
    }

    /// <summary>
    /// Gmail Archive is a special folder that is not visible in the Gmail web interface.
    /// We need to handle it separately.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task MapArchivedMailsAsync(CancellationToken cancellationToken)
    {
        var request = _gmailService.Users.Messages.List("me");
        request.Q = "in:archive";
        request.MaxResults = InitialMessageDownloadCountPerFolder;

        string pageToken = null;

        var archivedMessageIds = new List<string>();

        do
        {
            if (!string.IsNullOrEmpty(pageToken)) request.PageToken = pageToken;

            var response = await request.ExecuteAsync(cancellationToken);
            if (response.Messages == null) break;

            foreach (var message in response.Messages)
            {
                if (archivedMessageIds.Contains(message.Id)) continue;

                archivedMessageIds.Add(message.Id);
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        var result = await _gmailChangeProcessor.GetGmailArchiveComparisonResultAsync(archiveFolderId.Value, archivedMessageIds).ConfigureAwait(false);

        foreach (var archiveAddedItem in result.Added)
        {
            await HandleArchiveAssignmentAsync(archiveAddedItem);
        }

        foreach (var unAarchivedRemovedItem in result.Removed)
        {
            await HandleUnarchiveAssignmentAsync(unAarchivedRemovedItem);
        }
    }

    /// <summary>
    /// Maps existing Gmail Draft resources to local mail copies.
    /// This uses indexed search, therefore it's quite fast.
    /// It's safe to execute this after each Draft creation + batch message download.
    /// </summary>
    private async Task MapDraftIdsAsync(CancellationToken cancellationToken = default)
    {
        // Check if account has any draft locally.
        // There is no point to send this query if there are no local drafts.

        bool hasLocalDrafts = await _gmailChangeProcessor.HasAccountAnyDraftAsync(Account.Id).ConfigureAwait(false);

        if (!hasLocalDrafts) return;

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

    protected override Task<MailCopy> CreateMinimalMailCopyAsync(Message gmailMessage, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        bool isUnread = gmailMessage.GetIsUnread();
        bool isFocused = gmailMessage.GetIsFocused();
        bool isFlagged = gmailMessage.GetIsFlagged();
        bool isDraft = gmailMessage.GetIsDraft();

        // Try to get the most accurate date from Gmail's InternalDate first, then fallback to Date header
        DateTime creationDate = DateTime.UtcNow;

        if (gmailMessage.InternalDate.HasValue)
        {
            // Gmail's InternalDate is in milliseconds since Unix epoch
            creationDate = DateTimeOffset.FromUnixTimeMilliseconds(gmailMessage.InternalDate.Value).UtcDateTime;
        }
        else
        {
            // Fallback to parsing the Date header
            var dateHeaderValue = gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("Date", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrEmpty(dateHeaderValue) && DateTime.TryParse(dateHeaderValue, out var parsedDate))
            {
                creationDate = parsedDate.ToUniversalTime();
            }
        }

        // Extract From header and parse name/address
        var fromHeaderValue = gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("From", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        var (fromName, fromAddress) = ExtractNameAndEmailFromHeader(fromHeaderValue);

        // Detect calendar invitation by checking Content-Type header (only if calendar access granted)
        var itemType = Account.IsCalendarAccessGranted ? GetMailItemTypeFromHeaders(gmailMessage.Payload?.Headers) : MailItemType.Mail;

        var copy = new MailCopy()
        {
            CreationDate = creationDate,
            Subject = HttpUtility.HtmlDecode(gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("Subject", StringComparison.OrdinalIgnoreCase))?.Value ?? ""),
            FromName = HttpUtility.HtmlDecode(fromName),
            FromAddress = fromAddress,
            PreviewText = HttpUtility.HtmlDecode(gmailMessage.Snippet ?? "").Trim(),
            ThreadId = gmailMessage.ThreadId,
            Importance = MailImportance.Normal, // Default importance without MIME parsing
            Id = gmailMessage.Id,
            IsDraft = isDraft,
            HasAttachments = gmailMessage.Payload?.Parts?.Any(p => !string.IsNullOrEmpty(p.Filename)) ?? false,
            IsRead = !isUnread,
            IsFlagged = isFlagged,
            IsFocused = isFocused,
            InReplyTo = gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase))?.Value,
            MessageId = gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("Message-Id", StringComparison.OrdinalIgnoreCase))?.Value,
            References = gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("References", StringComparison.OrdinalIgnoreCase))?.Value,
            FileId = Guid.NewGuid(),
            ItemType = itemType
        };

        // Set DraftId if this is a draft
        if (copy.IsDraft)
            copy.DraftId = copy.ThreadId;

        return Task.FromResult(copy);
    }

    /// <summary>
    /// Determines MailItemType based on Gmail message headers.
    /// Gmail doesn't have EventMessage type like Outlook, but calendar invitations can be detected
    /// by checking Content-Type header for text/calendar or multipart/alternative with text/calendar part.
    /// </summary>
    private static MailItemType GetMailItemTypeFromHeaders(IList<MessagePartHeader> headers)
    {
        if (headers == null) return MailItemType.Mail;

        // Check Content-Type header for text/calendar
        var contentTypeHeader = headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        
        if (!string.IsNullOrEmpty(contentTypeHeader))
        {
            // Check if it's a calendar message (text/calendar or multipart with calendar)
            if (contentTypeHeader.Contains("text/calendar", StringComparison.OrdinalIgnoreCase))
            {
                // Check the METHOD parameter to determine invitation type
                var methodMatch = System.Text.RegularExpressions.Regex.Match(contentTypeHeader, @"method=([^;\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                if (methodMatch.Success)
                {
                    var method = methodMatch.Groups[1].Value.Trim('"').ToUpperInvariant();
                    
                    return method switch
                    {
                        "REQUEST" => MailItemType.CalendarInvitation,
                        "CANCEL" => MailItemType.CalendarCancellation,
                        "REPLY" => MailItemType.CalendarResponse,
                        _ => MailItemType.Mail
                    };
                }
                
                // If no method specified, assume it's an invitation
                return MailItemType.CalendarInvitation;
            }
        }

        return MailItemType.Mail;
    }

    /// <summary>
    /// Extracts name and email address from a header value like "Name <email@domain.com>" or "email@domain.com"
    /// </summary>
    private static (string name, string email) ExtractNameAndEmailFromHeader(string headerValue)
    {
        if (string.IsNullOrEmpty(headerValue))
            return ("", "");

        // Try to match "Name <email@domain.com>" format
        var match = System.Text.RegularExpressions.Regex.Match(headerValue, @"^(.+?)\s*<(.+?)>$");
        if (match.Success)
        {
            var name = match.Groups[1].Value.Trim().Trim('"');
            var email = match.Groups[2].Value.Trim();
            return (name, email);
        }

        // If no angle brackets, assume the whole value is the email with no name
        var emailOnly = headerValue.Trim();
        return ("", emailOnly);
    }

    /// <summary>
    /// Creates new mail packages for the given message.
    /// AssignedFolder is null since the LabelId is parsed out of the Message.
    /// NOTE: This method does NOT download MIME content during synchronization.
    /// MIME is only downloaded when user explicitly reads the message.
    /// </summary>
    /// <param name="message">Gmail message to create package for (must have Metadata format).</param>
    /// <param name="assignedFolder">Null, not used.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>New mail package that change processor can use to insert new mail into database.</returns>
    public override async Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(Message message,
                                                                              MailItemFolder assignedFolder,
                                                                              CancellationToken cancellationToken = default)
    {
        var packageList = new List<NewMailItemPackage>();

        // Create MailCopy from metadata only - NO MIME download
        var mailCopy = await CreateMinimalMailCopyAsync(message, assignedFolder, cancellationToken);

        // Check for local draft mapping using X-Wino-Draft-Id header from metadata
        if (mailCopy.IsDraft)
        {
            var draftIdHeader = message.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals(Domain.Constants.WinoLocalDraftHeader, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrEmpty(draftIdHeader) && Guid.TryParse(draftIdHeader, out Guid localDraftCopyUniqueId))
            {
                // This message belongs to existing local draft copy.
                // We don't need to create a new mail copy for this message, just update the existing one.

                bool isMappingSuccesfull = await _gmailChangeProcessor.MapLocalDraftAsync(Account.Id, localDraftCopyUniqueId, mailCopy.Id, mailCopy.DraftId, mailCopy.ThreadId);

                if (isMappingSuccesfull) return null;

                // Local copy doesn't exists. Continue execution to insert mail copy.
            }
        }

        if (message.LabelIds is not null)
        {
            foreach (var labelId in message.LabelIds)
            {
                // Pass null for MimeMessage - it will be downloaded later when user reads the mail
                packageList.Add(new NewMailItemPackage(mailCopy, null, labelId));
            }
        }

        return packageList;
    }

    #endregion

    #region Calendar Operations

    public override List<IRequestBundle<IClientServiceRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var calendarItem = request.Item;
        var attendees = request.Attendees;

        // Get the calendar for this event
        var calendar = calendarItem.AssignedCalendar;
        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        // Convert CalendarItem to Google Event
        var googleEvent = new Event
        {
            Summary = calendarItem.Title,
            Description = calendarItem.Description,
            Location = calendarItem.Location,
            Status = calendarItem.Status == CalendarItemStatus.Accepted ? "confirmed" : "tentative"
        };

        // Set start and end time
        if (calendarItem.IsAllDayEvent)
        {
            // All-day events use Date instead of DateTime
            googleEvent.Start = new EventDateTime
            {
                Date = calendarItem.StartDate.ToString("yyyy-MM-dd")
            };
            googleEvent.End = new EventDateTime
            {
                Date = calendarItem.EndDate.ToString("yyyy-MM-dd")
            };
        }
        else
        {
            // Regular events with time
            googleEvent.Start = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(calendarItem.StartDate, TimeSpan.Zero),
                TimeZone = calendarItem.StartTimeZone
            };
            googleEvent.End = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(calendarItem.EndDate, TimeSpan.Zero),
                TimeZone = calendarItem.EndTimeZone
            };
        }

        // Add attendees if any
        if (attendees != null && attendees.Count > 0)
        {
            googleEvent.Attendees = attendees.Select(a => new EventAttendee
            {
                Email = a.Email,
                DisplayName = a.Name,
                Optional = a.IsOptionalAttendee
            }).ToList();
        }

        // Create the insert request
        var insertRequest = _calendarService.Events.Insert(googleEvent, calendar.RemoteCalendarId);

        return [new HttpRequestBundle<IClientServiceRequest>(insertRequest, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> AcceptEvent(AcceptEventRequest request)
    {
        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        if (string.IsNullOrEmpty(calendarItem.RemoteEventId))
        {
            throw new InvalidOperationException("Cannot accept event without remote event ID");
        }

        // For Gmail, we need to patch the event with the user's response status
        // Get the current user's email from the account
        var userEmail = Account.Address;

        // Create a patch event to update only the attendee response
        var patchEvent = new Event();
        
        // We need to get the event first to update the specific attendee
        // However, for efficiency, we'll use the patch method with sendUpdates parameter
        var patchRequest = _calendarService.Events.Patch(new Event
        {
            // The API will handle updating the current user's attendee status
            Attendees = new List<EventAttendee>
            {
                new EventAttendee
                {
                    Email = userEmail,
                    ResponseStatus = "accepted"
                }
            }
        }, calendar.RemoteCalendarId, calendarItem.RemoteEventId);

        // Send updates to other attendees if there's a message
        patchRequest.SendUpdates = !string.IsNullOrEmpty(request.ResponseMessage) 
            ? Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.All 
            : Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IClientServiceRequest>(patchRequest, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> DeclineEvent(DeclineEventRequest request)
    {
        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        if (string.IsNullOrEmpty(calendarItem.RemoteEventId))
        {
            throw new InvalidOperationException("Cannot decline event without remote event ID");
        }

        var userEmail = Account.Address;

        var patchRequest = _calendarService.Events.Patch(new Event
        {
            Attendees = new List<EventAttendee>
            {
                new EventAttendee
                {
                    Email = userEmail,
                    ResponseStatus = "declined",
                    Comment = request.ResponseMessage
                }
            }
        }, calendar.RemoteCalendarId, calendarItem.RemoteEventId);

        patchRequest.SendUpdates = !string.IsNullOrEmpty(request.ResponseMessage) 
            ? Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.All 
            : Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IClientServiceRequest>(patchRequest, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> TentativeEvent(TentativeEventRequest request)
    {
        var calendarItem = request.Item;
        var calendar = calendarItem.AssignedCalendar;

        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        if (string.IsNullOrEmpty(calendarItem.RemoteEventId))
        {
            throw new InvalidOperationException("Cannot tentatively accept event without remote event ID");
        }

        var userEmail = Account.Address;

        var patchRequest = _calendarService.Events.Patch(new Event
        {
            Attendees = new List<EventAttendee>
            {
                new EventAttendee
                {
                    Email = userEmail,
                    ResponseStatus = "tentative",
                    Comment = request.ResponseMessage
                }
            }
        }, calendar.RemoteCalendarId, calendarItem.RemoteEventId);

        patchRequest.SendUpdates = !string.IsNullOrEmpty(request.ResponseMessage) 
            ? Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.All 
            : Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IClientServiceRequest>(patchRequest, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var calendarItem = request.Item;
        var attendees = request.Attendees;

        // Get the calendar for this event
        var calendar = calendarItem.AssignedCalendar;
        if (calendar == null)
        {
            throw new InvalidOperationException("Calendar item must have an assigned calendar");
        }

        if (string.IsNullOrEmpty(calendarItem.RemoteEventId))
        {
            throw new InvalidOperationException("Cannot update event without remote event ID");
        }

        // Convert CalendarItem to Google Event for update
        var googleEvent = new Event
        {
            Summary = calendarItem.Title,
            Description = calendarItem.Description,
            Location = calendarItem.Location,
            Status = calendarItem.Status == CalendarItemStatus.Accepted ? "confirmed" : "tentative",
            Transparency = calendarItem.ShowAs == CalendarItemShowAs.Free ? "transparent" : "opaque"
        };

        // Set start and end time with proper timezone handling
        // CalendarItem stores dates in the event's timezone (StartTimeZone/EndTimeZone)
        // When user edits in local timezone, the dates are already converted and stored correctly
        if (calendarItem.IsAllDayEvent)
        {
            // All-day events use Date instead of DateTime
            googleEvent.Start = new EventDateTime
            {
                Date = calendarItem.StartDate.ToString("yyyy-MM-dd")
            };
            googleEvent.End = new EventDateTime
            {
                Date = calendarItem.EndDate.ToString("yyyy-MM-dd")
            };
        }
        else
        {
            // Regular events with time
            // StartDate and EndDate are stored in the event's timezone
            // We preserve the timezone information during update
            googleEvent.Start = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(calendarItem.StartDate, TimeSpan.Zero),
                TimeZone = calendarItem.StartTimeZone ?? TimeZoneInfo.Local.Id
            };
            googleEvent.End = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(calendarItem.EndDate, TimeSpan.Zero),
                TimeZone = calendarItem.EndTimeZone ?? TimeZoneInfo.Local.Id
            };
        }

        // Add attendees if any
        if (attendees != null && attendees.Count > 0)
        {
            googleEvent.Attendees = attendees.Select(a => new EventAttendee
            {
                Email = a.Email,
                DisplayName = a.Name,
                Optional = a.IsOptionalAttendee
            }).ToList();
        }

        // Update the event using Google Calendar API
        var updateRequest = _calendarService.Events.Update(googleEvent, calendar.RemoteCalendarId, calendarItem.RemoteEventId);

        // Send notifications to attendees if the event has attendees
        updateRequest.SendUpdates = (attendees != null && attendees.Count > 0) 
            ? Google.Apis.Calendar.v3.EventsResource.UpdateRequest.SendUpdatesEnum.All 
            : Google.Apis.Calendar.v3.EventsResource.UpdateRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IClientServiceRequest>(updateRequest, request)];
    }

    #endregion

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync();

        _gmailService.Dispose();
        _peopleService.Dispose();
        _calendarService.Dispose();
        _googleHttpClient.Dispose();
    }
}

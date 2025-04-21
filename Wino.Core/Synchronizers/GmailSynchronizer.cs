using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Google;
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
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Http;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;
using Wino.Services;
using CalendarService = Google.Apis.Calendar.v3.CalendarService;

namespace Wino.Core.Synchronizers.Mail;

public class GmailSynchronizer : WinoSynchronizer<IClientServiceRequest, Message, Event>, IHttpClientFactory
{
    public override uint BatchModificationSize => 1000;

    /// This now represents actual per-folder download count for initial sync
    public override uint InitialMessageDownloadCountPerFolder => 1500;

    // It's actually 100. But Gmail SDK has internal bug for Out of Memory exception.
    // https://github.com/googleapis/google-api-dotnet-client/issues/2603
    private const uint MaximumAllowedBatchRequestSize = 10;

    private readonly ConfigurableHttpClient _googleHttpClient;
    private readonly GmailService _gmailService;
    private readonly CalendarService _calendarService;
    private readonly PeopleServiceService _peopleService;

    private readonly IGmailChangeProcessor _gmailChangeProcessor;
    private readonly ILogger _logger = Log.ForContext<GmailSynchronizer>();

    // Keeping a reference for quick access to the virtual archive folder.
    private Guid? archiveFolderId;

    public GmailSynchronizer(MailAccount account,
                             IGmailAuthenticator authenticator,
                             IGmailChangeProcessor gmailChangeProcessor) : base(account)
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
        }

        // There is no specific folder synchronization in Gmail.
        // Therefore we need to stop the synchronization at this point
        // if type is only folder metadata sync.

        if (options.Type == MailSynchronizationType.FoldersOnly) return MailSynchronizationResult.Empty;

        retry:
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
            // Get all folders that need synchronization
            var folders = await _gmailChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var syncFolders = folders.Where(f =>
                f.IsSynchronizationEnabled &&
                f.SpecialFolderType != SpecialFolderType.Category &&
                f.SpecialFolderType != SpecialFolderType.Archive).ToList();

            // Download messages for each folder separately
            foreach (var folder in syncFolders)
            {
                var messageRequest = _gmailService.Users.Messages.List("me");
                messageRequest.MaxResults = InitialMessageDownloadCountPerFolder;
                messageRequest.LabelIds = new[] { folder.RemoteFolderId };
                // messageRequest.OrderBy = "internalDate desc"; // Get latest messages first
                messageRequest.IncludeSpamTrash = true;

                string nextPageToken = null;
                uint downloadedCount = 0;

                do
                {
                    if (!string.IsNullOrEmpty(nextPageToken))
                    {
                        messageRequest.PageToken = nextPageToken;
                    }

                    var result = await messageRequest.ExecuteAsync(cancellationToken);
                    nextPageToken = result.NextPageToken;

                    if (result.Messages != null)
                    {
                        downloadedCount += (uint)result.Messages.Count;
                        listChanges.Add(result);
                    }

                    // Stop if we've downloaded enough messages for this folder
                    if (downloadedCount >= InitialMessageDownloadCountPerFolder)
                    {
                        break;
                    }

                } while (!string.IsNullOrEmpty(nextPageToken));

                _logger.Information("Downloaded {Count} messages for folder {Folder}", downloadedCount, folder.FolderName);
            }
        }
        else
        {
            var startHistoryId = ulong.Parse(Account.SynchronizationDeltaIdentifier);
            var nextPageToken = ulong.Parse(Account.SynchronizationDeltaIdentifier).ToString();

            var historyRequest = _gmailService.Users.History.List("me");
            historyRequest.StartHistoryId = startHistoryId;

            try
            {
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
            catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // History ID is too old or expired, need to do a full sync.
                // Theoratically we need to delete the local cache and start from scratch.

                _logger.Warning("History ID {StartHistoryId} is expired for {Name}. Will remove user's mail cache and do full sync.", startHistoryId, Account.Name);

                await _gmailChangeProcessor.DeleteUserMailCacheAsync(Account.Id).ConfigureAwait(false);

                Account.SynchronizationDeltaIdentifier = string.Empty;

                await _gmailChangeProcessor.UpdateAccountAsync(Account).ConfigureAwait(false);

                goto retry;
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

        // Start downloading missing messages.
        foreach (var messageId in missingMessageIds)
        {
            await DownloadSingleMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        }

        // Map archive assignments if there are any changes reported.
        if (listChanges.Any() || deltaChanges.Any())
        {
            await MapArchivedMailsAsync(cancellationToken).ConfigureAwait(false);
        }

        // Map remote drafts to local drafts.
        await MapDraftIdsAsync(cancellationToken).ConfigureAwait(false);

        // Start processing delta changes.
        foreach (var historyResponse in deltaChanges)
        {
            await ProcessHistoryChangesAsync(historyResponse).ConfigureAwait(false);
        }

        // Take the max history id from delta changes and update the account sync modifier.

        if (deltaChanges.Any())
        {
            var maxHistoryId = deltaChanges.Where(a => a.HistoryId != null).Max(a => a.HistoryId);

            await UpdateAccountSyncIdentifierAsync(maxHistoryId);

            if (maxHistoryId != null)
            {
                // TODO: This is not good. Centralize the identifier fetch and prevent direct access here.
                // Account.SynchronizationDeltaIdentifier = await _gmailChangeProcessor.UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, maxHistoryId.ToString()).ConfigureAwait(false);

                _logger.Debug("Final sync identifier {SynchronizationDeltaIdentifier}", Account.SynchronizationDeltaIdentifier);
            }
        }

        // Get all unred new downloaded items and return in the result.
        // This is primarily used in notifications.

        var unreadNewItems = await _gmailChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, missingMessageIds).ConfigureAwait(false);

        return MailSynchronizationResult.Completed(unreadNewItems);
    }

    private async Task DownloadSingleMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        // Google .NET SDK has memory issues with batch downloading messages which will not be fixed since the library is in maintenance mode.
        // https://github.com/googleapis/google-api-dotnet-client/issues/2603
        // This method will be used to download messages one by one to prevent memory spikes.

        try
        {
            var singleRequest = CreateSingleMessageGet(messageId);
            var downloadedMessage = await singleRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            await HandleSingleItemDownloadedCallbackAsync(downloadedMessage, null, messageId, cancellationToken).ConfigureAwait(false);
            await UpdateAccountSyncIdentifierAsync(downloadedMessage.HistoryId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while downloading message {MessageId} for {Name}", messageId, Account.Name);
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

        // Download missing messages.
        foreach (var messageId in downloadRequireMessageIds)
        {
            await DownloadSingleMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        }

        // Get results from database and return.

        return await _gmailChangeProcessor.GetMailCopiesAsync(messageIds);
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

    private void ProcessGmailRequestError(RequestError error, IRequestBundle<IClientServiceRequest> bundle)
    {
        if (error == null) return;

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

    /// <summary>
    /// Handles after each single message download.
    /// This involves adding the Gmail message into Wino database.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="error"></param>
    /// <param name="httpResponseMessage"></param>
    /// <param name="cancellationToken"></param>
    private async Task<Message> HandleSingleItemDownloadedCallbackAsync(Message message,
                                                               RequestError error,
                                                               string downloadingMessageId,
                                                               CancellationToken cancellationToken = default)
    {
        try
        {
            ProcessGmailRequestError(error, null);
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

            return null;
        }

        // Gmail has LabelId property for each message.
        // Therefore we can pass null as the assigned folder safely.
        var mailPackage = await CreateNewMailPackagesAsync(message, null, cancellationToken);

        // If CreateNewMailPackagesAsync returns null it means local draft mapping is done.
        // We don't need to insert anything else.
        if (mailPackage == null) return message;

        foreach (var package in mailPackage)
        {
            await _gmailChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false);
        }

        return message;
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
        ProcessGmailRequestError(error, bundle);

        if (bundle is HttpRequestBundle<IClientServiceRequest, Message> messageBundle)
        {
            var gmailMessage = await messageBundle.DeserializeBundleAsync(httpResponseMessage, cancellationToken).ConfigureAwait(false);

            if (gmailMessage == null) return;

            await HandleSingleItemDownloadedCallbackAsync(gmailMessage, error, string.Empty, cancellationToken);
            await UpdateAccountSyncIdentifierAsync(gmailMessage.HistoryId).ConfigureAwait(false);
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

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync();

        _gmailService.Dispose();
        _peopleService.Dispose();
        _calendarService.Dispose();
        _googleHttpClient.Dispose();
    }
}

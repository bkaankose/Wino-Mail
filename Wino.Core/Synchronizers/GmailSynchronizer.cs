using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using CommunityToolkit.Mvvm.Messaging;
using Google;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Http;
using Google.Apis.PeopleService.v1;
using Google.Apis.Requests;
using Google.Apis.Services;
using Google.Apis.Upload;
using MailKit;
using Microsoft.IdentityModel.Tokens;
using MimeKit;
using MoreLinq;
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
using Wino.Core.Integration.Processors;
using Wino.Core.Misc;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;
using Wino.Services;
using CalendarService = Google.Apis.Calendar.v3.CalendarService;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using DriveService = Google.Apis.Drive.v3.DriveService;

namespace Wino.Core.Synchronizers.Mail;

[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Label))]
[JsonSerializable(typeof(Draft))]
[JsonSerializable(typeof(Event))]
public partial class GmailSynchronizerJsonContext : JsonSerializerContext;

/// <summary>
/// Gmail synchronizer implementation using Gmail History API for efficient incremental sync.
///
/// SYNCHRONIZATION STRATEGY:
/// - Initial sync: Downloads up to 15c00 messages PER FOLDER with metadata only.
///   Uses a global HashSet to track downloaded message IDs, avoiding duplicate downloads
///   when messages have multiple labels. Each folder gets its full quota of messages.
/// - Incremental sync: Uses ONLY History API to get changes since last sync.
///   No per-folder downloads during incremental sync - this is the proper Gmail sync approach.
/// - Messages are downloaded with metadata only during initial sync (no MIME content)
/// - New messages during incremental sync are downloaded with full MIME content
/// - MIME files for initial sync messages are downloaded on-demand when user reads a message
///
/// Key implementation details:
/// - PerformInitialSyncAsync: Downloads messages per-folder with global deduplication
/// - SynchronizeDeltaAsync: Processes incremental changes using History API with pagination
/// - Handles 404/410 errors (history expired) by triggering full resync
/// - CreateMinimalMailCopyAsync: Extracts MailCopy fields from Gmail Metadata format
/// - DownloadMissingMimeMessageAsync: Downloads raw MIME only when explicitly requested
/// </summary>
public class GmailSynchronizer : WinoSynchronizer<IClientServiceRequest, Message, Event>, IHttpClientFactory
{
    public override uint BatchModificationSize => 1000;

    /// <summary>
    /// Legacy page size hint kept for compatibility with shared synchronizer contracts.
    /// Gmail initial sync now downloads all messages inside the selected cutoff window.
    /// </summary>
    public override uint InitialMessageDownloadCountPerFolder => 1500;

    // It's actually 100. But Gmail SDK has internal bug for Out of Memory exception.
    // https://github.com/googleapis/google-api-dotnet-client/issues/2603
    private const uint MaximumAllowedBatchRequestSize = 10;

    private readonly ConfigurableHttpClient _googleHttpClient;
    private readonly GmailService _gmailService;
    private readonly CalendarService _calendarService;
    private readonly DriveService _driveService;
    private readonly PeopleServiceService _peopleService;

    private readonly IGmailChangeProcessor _gmailChangeProcessor;
    private readonly IGmailSynchronizerErrorHandlerFactory _gmailSynchronizerErrorHandlerFactory;
    private readonly ILogger _logger = Log.ForContext<GmailSynchronizer>();

    // Keeping a reference for quick access to the virtual archive folder.
    private Guid? archiveFolderId;
    private bool _isFolderStructureChanged;

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
        _driveService = new DriveService(initializer);

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

        var downloadedMessageIds = new List<string>();
        var folderResults = new List<FolderSyncResult>();

        try
        {
            _isFolderStructureChanged = false;

            // Make sure that virtual archive folder exists before all.
            if (!archiveFolderId.HasValue)
                await InitializeArchiveFolderAsync().ConfigureAwait(false);

            // Gmail must always synchronize folders before because it doesn't have a per-folder sync.
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

            if (_isFolderStructureChanged)
            {
                WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
            }

            _logger.Information("Synchronizing folders for {Name} is completed", Account.Name);
            UpdateSyncProgress(0, 0, "Folders synchronized");

            // Stop synchronization at this point if type is only folder metadata sync.
            if (options.Type == MailSynchronizationType.FoldersOnly) return MailSynchronizationResult.Empty;

            cancellationToken.ThrowIfCancellationRequested();

            bool isInitialSync = string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier);

            _logger.Debug("Is initial synchronization: {IsInitialSync}", isInitialSync);

            if (isInitialSync)
            {
                // INITIAL SYNC: Download all messages globally (not per-folder) to avoid duplicates.
                // Gmail messages can have multiple labels, so per-folder download would fetch same message multiple times.
                downloadedMessageIds = await PerformInitialSyncAsync(cancellationToken).ConfigureAwait(false);

                // Set the history ID to the latest value after initial sync
                UpdateSyncProgress(0, 0, "Finalizing synchronization...");
                var profile = await _gmailService.Users.GetProfile("me").ExecuteAsync(cancellationToken);
                if (profile.HistoryId.HasValue)
                {
                    await UpdateAccountSyncIdentifierAsync(profile.HistoryId.Value).ConfigureAwait(false);
                    _logger.Information("Initial sync completed. Set history ID to {HistoryId}", profile.HistoryId.Value);
                }

                // Create successful folder results for all folders
                var allFolders = await _gmailChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);
                foreach (var folder in allFolders.Where(f => f.RemoteFolderId != ServiceConstants.ARCHIVE_LABEL_ID))
                {
                    folderResults.Add(FolderSyncResult.Successful(folder.Id, folder.FolderName, 0));
                }
            }
            else
            {
                // INCREMENTAL SYNC: Use ONLY History API - no per-folder downloads.
                // This is the proper Gmail sync strategy as recommended by Google.
                UpdateSyncProgress(0, 0, "Synchronizing changes...");
                var deltaResult = await SynchronizeDeltaAsync(options, cancellationToken).ConfigureAwait(false);
                downloadedMessageIds.AddRange(deltaResult.DownloadedMessageIds);

                // If history sync was reset due to expired history ID, we need to do initial sync
                if (deltaResult.RequiresFullResync)
                {
                    _logger.Warning("History ID expired. Performing full resync for {Name}", Account.Name);
                    downloadedMessageIds = await PerformInitialSyncAsync(cancellationToken).ConfigureAwait(false);

                    // Update history ID after full resync
                    var profile = await _gmailService.Users.GetProfile("me").ExecuteAsync(cancellationToken);
                    if (profile.HistoryId.HasValue)
                    {
                        await UpdateAccountSyncIdentifierAsync(profile.HistoryId.Value).ConfigureAwait(false);
                        _logger.Information("Full resync completed. Set history ID to {HistoryId}", profile.HistoryId.Value);
                    }
                }

                UpdateSyncProgress(0, 0, "Changes synchronized");

                // Create folder results for incremental sync
                var allFolders = await _gmailChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false);
                foreach (var folder in allFolders.Where(f => f.RemoteFolderId != ServiceConstants.ARCHIVE_LABEL_ID))
                {
                    folderResults.Add(FolderSyncResult.Successful(folder.Id, folder.FolderName, 0));
                }
            }

            // Map Gmail Draft resource IDs for all drafts.
            // Gmail's Messages API doesn't expose Draft IDs, so we query the Drafts API separately.
            // This ensures DraftId is correctly set for both Wino-created and externally-created drafts.
            await MapDraftIdsAsync(cancellationToken).ConfigureAwait(false);

            // Keep virtual Archive folder assignments in sync with Gmail "in:archive" query.
            try
            {
                await MapArchivedMailsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to map Gmail archive folder for {Name}", Account.Name);
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

        // Get all unread new downloaded items for notifications
        var unreadNewItems = await _gmailChangeProcessor.GetDownloadedUnreadMailsAsync(Account.Id, downloadedMessageIds).ConfigureAwait(false);

        return MailSynchronizationResult.CompletedWithFolderResults(unreadNewItems, folderResults);
    }

    /// <summary>
    /// Result of delta synchronization using History API.
    /// </summary>
    private record DeltaSyncResult(List<string> DownloadedMessageIds, bool RequiresFullResync);

    /// <summary>
    /// Performs initial synchronization by downloading messages per-folder.
    /// Messages are filtered by the account's configured initial synchronization cutoff date when present,
    /// and duplicates are avoided globally because Gmail messages can have multiple labels.
    /// </summary>
    private async Task<List<string>> PerformInitialSyncAsync(CancellationToken cancellationToken)
    {
        // Track all downloaded message IDs globally to avoid duplicate downloads
        var downloadedMessageIds = new HashSet<string>();
        var referenceDateUtc = Account.CreatedAt ?? DateTime.UtcNow;
        var initialSynchronizationCutoffDateUtc = Account.InitialSynchronizationRange.ToCutoffDateUtc(referenceDateUtc);
        var queryText = initialSynchronizationCutoffDateUtc.HasValue
            ? $"after:{initialSynchronizationCutoffDateUtc.Value.ToUniversalTime():yyyy/MM/dd}"
            : null;

        _logger.Information("Performing initial sync for {Name} - downloading messages per folder", Account.Name);

        try
        {
            // Get all folders to sync (exclude virtual ARCHIVE folder)
            var folders = await _gmailChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var syncableFolders = folders
                .Where(f => f.IsSynchronizationEnabled && f.RemoteFolderId != ServiceConstants.ARCHIVE_LABEL_ID)
                .OrderByDescending(f => f.SpecialFolderType == SpecialFolderType.Draft || f.RemoteFolderId == ServiceConstants.DRAFT_LABEL_ID)
                .ToList();

            var totalFolders = syncableFolders.Count;
            var totalMessagesDownloaded = 0;

            for (int i = 0; i < totalFolders; i++)
            {
                var folder = syncableFolders[i];
                cancellationToken.ThrowIfCancellationRequested();

                UpdateSyncProgress(totalFolders, totalFolders - (i + 1), $"Syncing {folder.FolderName}...");

                _logger.Debug("Downloading messages for folder {FolderName} (label: {LabelId})", folder.FolderName, folder.RemoteFolderId);

                var folderDownloaded = 0;
                string pageToken = null;

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var request = _gmailService.Users.Messages.List("me");
                    request.LabelIds = new Google.Apis.Util.Repeatable<string>(new[] { folder.RemoteFolderId });
                    request.MaxResults = 500; // API max is 500
                    request.PageToken = pageToken;
                    request.Q = queryText;

                    var response = await request.ExecuteAsync(cancellationToken);

                    if (response.Messages != null && response.Messages.Count > 0)
                    {
                        // Filter out already downloaded messages to avoid duplicates
                        var newMessageIds = response.Messages
                            .Select(m => m.Id)
                            .Where(id => !downloadedMessageIds.Contains(id))
                            .ToList();

                        if (newMessageIds.Count > 0)
                        {
                            // Draft folder needs MIME during initial sync so compose can open immediately.
                            bool shouldDownloadRawMime = folder.SpecialFolderType == SpecialFolderType.Draft || folder.RemoteFolderId == ServiceConstants.DRAFT_LABEL_ID;
                            await DownloadMessagesInBatchAsync(newMessageIds, downloadRawMime: shouldDownloadRawMime, cancellationToken).ConfigureAwait(false);

                            foreach (var id in newMessageIds)
                            {
                                downloadedMessageIds.Add(id);
                            }

                            folderDownloaded += newMessageIds.Count;
                            totalMessagesDownloaded += newMessageIds.Count;
                        }

                        _logger.Debug("Folder {FolderName}: Downloaded {New} new messages ({Total} total in folder)",
                            folder.FolderName, newMessageIds.Count, folderDownloaded);
                    }

                    pageToken = response.NextPageToken;

                } while (!string.IsNullOrEmpty(pageToken));

                _logger.Information("Folder {FolderName}: Downloaded {Count} messages", folder.FolderName, folderDownloaded);
                UpdateSyncProgress(totalFolders, 0, Translator.SyncAction_SynchronizingAccount);
            }

            _logger.Information("Initial sync completed. Downloaded {Count} unique messages for {Name}", downloadedMessageIds.Count, Account.Name);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.Warning("Rate limit exceeded during initial sync. Retrying after delay.");
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during initial sync for {Name}", Account.Name);
            throw;
        }

        return downloadedMessageIds.ToList();
    }

    /// <summary>
    /// Performs incremental synchronization using Gmail History API.
    /// This is the recommended approach for Gmail sync after initial sync is complete.
    /// Returns a result indicating downloaded messages and whether a full resync is needed.
    /// </summary>
    private async Task<DeltaSyncResult> SynchronizeDeltaAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var downloadedMessageIds = new List<string>();

        try
        {
            string pageToken = null;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var historyRequest = _gmailService.Users.History.List("me");
                historyRequest.StartHistoryId = ulong.Parse(Account.SynchronizationDeltaIdentifier!);

                if (!string.IsNullOrEmpty(pageToken))
                    historyRequest.PageToken = pageToken;

                var historyResponse = await historyRequest.ExecuteAsync(cancellationToken);

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
                    // During delta sync, download with Raw format to get MIME content for new messages
                    if (addedMessageIds.Count != 0)
                    {
                        // Deduplicate message IDs
                        var uniqueAddedIds = addedMessageIds.Distinct().ToList();
                        await DownloadMessagesInBatchAsync(uniqueAddedIds, downloadRawMime: true, cancellationToken).ConfigureAwait(false);
                        downloadedMessageIds.AddRange(uniqueAddedIds);
                    }

                    // Process other history changes (label changes, deletions)
                    await ProcessHistoryChangesAsync(historyResponse).ConfigureAwait(false);
                }

                // CRITICAL: Update the history ID to the latest one after processing all changes
                // History IDs are always incremental, so the response contains the latest history ID
                if (historyResponse.HistoryId.HasValue)
                {
                    await UpdateAccountSyncIdentifierAsync(historyResponse.HistoryId.Value).ConfigureAwait(false);
                    _logger.Debug("Updated history ID to {HistoryId} after delta sync", historyResponse.HistoryId.Value);
                }

                pageToken = historyResponse.NextPageToken;

            } while (!string.IsNullOrEmpty(pageToken));

            _logger.Information("Delta sync completed. Downloaded {Count} new messages for {Name}", downloadedMessageIds.Count, Account.Name);

            return new DeltaSyncResult(downloadedMessageIds, RequiresFullResync: false);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound ||
                                            (int)ex.HttpStatusCode == 410) // Gone - history expired
        {
            // History ID is no longer valid (expired or not found)
            // This happens when:
            // 1. The history ID is too old (Gmail keeps history for ~30 days)
            // 2. The account was reset or history was cleared
            // Reset the sync identifier and signal that a full resync is needed
            _logger.Warning("History ID {HistoryId} expired or not found for {Name}. Full resync required. Error: {Error}",
                Account.SynchronizationDeltaIdentifier, Account.Name, ex.Message);

            // Clear the sync identifier to trigger initial sync
            Account.SynchronizationDeltaIdentifier = await _gmailChangeProcessor
                .UpdateAccountDeltaSynchronizationIdentifierAsync(Account.Id, null)
                .ConfigureAwait(false);

            return new DeltaSyncResult(downloadedMessageIds, RequiresFullResync: true);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.Warning("Rate limit exceeded during delta sync for {Name}. Retrying after delay.", Account.Name);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw;
        }
    }

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        _logger.Information("Internal calendar synchronization started for {Name}", Account.Name);

        cancellationToken.ThrowIfCancellationRequested();

        await SynchronizeCalendarsAsync(cancellationToken).ConfigureAwait(false);

        if (options?.Type == CalendarSynchronizationType.CalendarMetadata)
            return CalendarSynchronizationResult.Empty;

        bool isInitialSync = string.IsNullOrEmpty(Account.SynchronizationDeltaIdentifier);

        _logger.Debug("Is initial synchronization: {IsInitialSync}", isInitialSync);

        var localCalendars = (await _gmailChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false))
            .Where(c => c.IsSynchronizationEnabled)
            .ToList();

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
                var request = _calendarService.Events.List(calendar.RemoteCalendarId);

                // Fetch individual event instances (including recurring event occurrences)
                // rather than recurring event masters. This ensures we get all occurrences
                // as separate events that can be stored and displayed directly.
                request.SingleEvents = true;
                request.ShowDeleted = true;

                if (!string.IsNullOrEmpty(calendar.SynchronizationDeltaToken))
                {
                    request.SyncToken = calendar.SynchronizationDeltaToken;
                }
                else
                {
                    request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow.AddYears(-1);
                }

                string nextPageToken;
                string syncToken;

                var allEvents = new List<Event>();

                do
                {
                    var events = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                    if (events.Items != null)
                    {
                        allEvents.AddRange(events.Items);
                    }

                    nextPageToken = events.NextPageToken;
                    syncToken = events.NextSyncToken;
                    request.PageToken = nextPageToken;
                }
                while (!string.IsNullOrEmpty(nextPageToken));

                calendar.SynchronizationDeltaToken = syncToken;

                var eventByRemoteId = allEvents
                    .Where(e => !string.IsNullOrWhiteSpace(e.Id))
                    .GroupBy(e => e.Id, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                foreach (var @event in OrderCalendarEventsForPersistence(allEvents))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        await EnsureRecurringParentProcessedAsync(calendar, @event, eventByRemoteId, cancellationToken).ConfigureAwait(false);
                        await _gmailChangeProcessor.ManageCalendarEventAsync(@event, calendar, Account).ConfigureAwait(false);
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

                        _ = await _gmailSynchronizerErrorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                        CaptureSynchronizationIssue(errorContext);
                        _logger.Error(ex, "Failed to process Gmail event {EventId} for calendar {CalendarName}", @event.Id, calendar.Name);
                    }
                }

                await _gmailChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
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

                _ = await _gmailSynchronizerErrorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                CaptureSynchronizationIssue(errorContext);

                if (!errorContext.CanContinueSync)
                    throw;

                UpdateSyncProgress(totalCalendars, totalCalendars - (i + 1), Translator.SyncAction_SynchronizingCalendarEvents);
            }
        }

        return CalendarSynchronizationResult.Empty;
    }

    private static IEnumerable<Event> OrderCalendarEventsForPersistence(IEnumerable<Event> events)
        => events
            .OrderBy(e => !string.IsNullOrWhiteSpace(e.RecurringEventId))
            .ThenByDescending(e => !string.IsNullOrWhiteSpace(GoogleIntegratorExtensions.GetRecurrenceString(e)))
            .ThenBy(e => GoogleIntegratorExtensions.GetEventDateTimeOffset(e.Start) ?? DateTimeOffset.MinValue);

    private async Task EnsureRecurringParentProcessedAsync(
        AccountCalendar calendar,
        Event calendarEvent,
        Dictionary<string, Event> eventByRemoteId,
        CancellationToken cancellationToken)
    {
        var recurringEventId = calendarEvent?.RecurringEventId;
        if (string.IsNullOrWhiteSpace(recurringEventId))
            return;

        var parentItem = await _gmailChangeProcessor.GetCalendarItemAsync(calendar.Id, recurringEventId).ConfigureAwait(false);
        if (parentItem != null)
            return;

        if (!eventByRemoteId.TryGetValue(recurringEventId, out var parentEvent))
        {
            try
            {
                parentEvent = await _calendarService.Events.Get(calendar.RemoteCalendarId, recurringEventId)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                _logger.Warning(ex,
                    "Failed to fetch recurring parent {ParentRemoteEventId} for child {ChildRemoteEventId} in calendar {CalendarName}",
                    recurringEventId,
                    calendarEvent.Id,
                    calendar.Name);
            }

            if (parentEvent != null && !string.IsNullOrWhiteSpace(parentEvent.Id))
            {
                eventByRemoteId[parentEvent.Id] = parentEvent;
            }
        }

        if (parentEvent == null)
        {
            _logger.Warning(
                "Recurring parent {ParentRemoteEventId} is still missing for child {ChildRemoteEventId} in calendar {CalendarName}",
                recurringEventId,
                calendarEvent.Id,
                calendar.Name);
            return;
        }

        await _gmailChangeProcessor.ManageCalendarEventAsync(parentEvent, calendar, Account).ConfigureAwait(false);
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
        var remotePrimaryCalendarId = GetPrimaryCalendarId(calendarListResponse.Items);
        var usedCalendarColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                var remoteBackgroundColor = GetRemoteGmailCalendarBackgroundColor(calendar);
                var fallbackColor = ColorHelpers.GetDistinctFlatColorHex(usedCalendarColors, remoteBackgroundColor);
                var localCalendar = calendar.AsCalendar(Account.Id, fallbackColor);
                localCalendar.IsPrimary = string.Equals(localCalendar.RemoteCalendarId, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
                localCalendar.BackgroundColorHex = ResolveSynchronizedCalendarBackgroundColor(remoteBackgroundColor, localCalendar, usedCalendarColors);
                localCalendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(localCalendar.BackgroundColorHex);
                usedCalendarColors.Add(localCalendar.BackgroundColorHex);
                insertedCalendars.Add(localCalendar);
            }
            else
            {
                // Update existing calendar. Right now we only update the name.
                var resolvedColor = ResolveSynchronizedCalendarBackgroundColor(GetRemoteGmailCalendarBackgroundColor(calendar), existingLocalCalendar, usedCalendarColors);
                if (ShouldUpdateCalendar(calendar, existingLocalCalendar, remotePrimaryCalendarId) ||
                    !string.Equals(existingLocalCalendar.BackgroundColorHex, resolvedColor, StringComparison.OrdinalIgnoreCase))
                {
                    existingLocalCalendar.Name = calendar.Summary;
                    existingLocalCalendar.TimeZone = calendar.TimeZone;
                    existingLocalCalendar.BackgroundColorHex = resolvedColor;
                    existingLocalCalendar.TextColorHex = ColorHelpers.GetReadableTextColorHex(existingLocalCalendar.BackgroundColorHex);
                    existingLocalCalendar.IsPrimary = string.Equals(existingLocalCalendar.RemoteCalendarId, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
                    existingLocalCalendar.IsReadOnly = !string.Equals(calendar.AccessRole, "owner", StringComparison.OrdinalIgnoreCase)
                                                      && !string.Equals(calendar.AccessRole, "writer", StringComparison.OrdinalIgnoreCase);

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
            _isFolderStructureChanged = true;

            // Migration-> User might've already have another special folder for Archive.
            // We must remove that type assignment.
            // This code can be removed after sometime.

            var otherArchiveFolders = localFolders.Where(a => a.SpecialFolderType == SpecialFolderType.Archive && a.Id != archiveFolderId.Value).ToList();

            if (otherArchiveFolders.Any())
            {
                _isFolderStructureChanged = true;
            }

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
                    existingLocalFolder.FolderName = GoogleIntegratorExtensions.GetFolderName(remoteFolder.Name);
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
            _isFolderStructureChanged = true;
        }
    }

    private bool ShouldUpdateCalendar(CalendarListEntry calendarListEntry, AccountCalendar accountCalendar, string remotePrimaryCalendarId)
    {
        var remoteCalendarName = calendarListEntry.Summary;
        var remoteTimeZone = calendarListEntry.TimeZone;
        var remoteBackgroundColor = ResolveSynchronizedCalendarBackgroundColor(GetRemoteGmailCalendarBackgroundColor(calendarListEntry), accountCalendar);
        var remoteTextColor = ColorHelpers.GetReadableTextColorHex(remoteBackgroundColor);
        var remoteIsPrimary = string.Equals(calendarListEntry.Id, remotePrimaryCalendarId, StringComparison.OrdinalIgnoreCase);
        var remoteIsReadOnly = !string.Equals(calendarListEntry.AccessRole, "owner", StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(calendarListEntry.AccessRole, "writer", StringComparison.OrdinalIgnoreCase);

        bool isNameChanged = !string.Equals(accountCalendar.Name, remoteCalendarName, StringComparison.OrdinalIgnoreCase);
        bool isTimeZoneChanged = !string.Equals(accountCalendar.TimeZone, remoteTimeZone, StringComparison.OrdinalIgnoreCase);
        bool isBackgroundColorChanged = !string.Equals(accountCalendar.BackgroundColorHex, remoteBackgroundColor, StringComparison.OrdinalIgnoreCase);
        bool isTextColorChanged = !string.Equals(accountCalendar.TextColorHex, remoteTextColor, StringComparison.OrdinalIgnoreCase);
        bool isPrimaryChanged = accountCalendar.IsPrimary != remoteIsPrimary;
        bool isReadOnlyChanged = accountCalendar.IsReadOnly != remoteIsReadOnly;

        return isNameChanged || isTimeZoneChanged || isBackgroundColorChanged || isTextColorChanged || isPrimaryChanged || isReadOnlyChanged;
    }

    private static string GetRemoteGmailCalendarBackgroundColor(CalendarListEntry calendarListEntry)
        => string.IsNullOrWhiteSpace(calendarListEntry?.BackgroundColor) ? null : calendarListEntry.BackgroundColor;

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

    private string GetPrimaryCalendarId(IList<CalendarListEntry> remoteCalendars)
    {
        if (remoteCalendars == null || remoteCalendars.Count == 0)
            return string.Empty;

        var explicitPrimary = remoteCalendars.FirstOrDefault(c => c.Primary.GetValueOrDefault());
        if (explicitPrimary != null)
            return explicitPrimary.Id;

        var byPrimaryKeyword = remoteCalendars.FirstOrDefault(c => string.Equals(c.Id, "primary", StringComparison.OrdinalIgnoreCase));
        if (byPrimaryKeyword != null)
            return byPrimaryKeyword.Id;

        var byAccountAddress = remoteCalendars.FirstOrDefault(c => string.Equals(c.Id, Account.Address, StringComparison.OrdinalIgnoreCase));
        if (byAccountAddress != null)
            return byAccountAddress.Id;

        return remoteCalendars.First().Id;
    }

    private bool ShouldUpdateFolder(Label remoteFolder, MailItemFolder existingLocalFolder)
    {
        var remoteFolderName = GoogleIntegratorExtensions.GetFolderName(remoteFolder.Name);
        var localFolderName = existingLocalFolder.FolderName ?? string.Empty;

        bool isNameChanged = !localFolderName.Equals(remoteFolderName, StringComparison.Ordinal);
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
            // ARCHIVE is a virtual folder - handle it separately
            if (labelId == ServiceConstants.ARCHIVE_LABEL_ID)
            {
                await HandleArchiveAssignmentAsync(messageId).ConfigureAwait(false);
                continue;
            }

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
            // ARCHIVE is a virtual folder - handle it separately
            if (labelId == ServiceConstants.ARCHIVE_LABEL_ID)
            {
                await HandleUnarchiveAssignmentAsync(messageId).ConfigureAwait(false);
                continue;
            }

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

    public override List<IRequestBundle<IClientServiceRequest>> ChangeJunkState(BatchChangeJunkStateRequest request)
    {
        bool isJunk = request[0].IsJunk;

        var addLabelIds = new HashSet<string>();
        var removeLabelIds = new HashSet<string>();

        if (isJunk)
        {
            addLabelIds.Add(ServiceConstants.SPAM_LABEL_ID);
            removeLabelIds.Add(ServiceConstants.INBOX_LABEL_ID);
        }
        else
        {
            addLabelIds.Add(ServiceConstants.INBOX_LABEL_ID);
            removeLabelIds.Add(ServiceConstants.SPAM_LABEL_ID);
        }

        var batchModifyRequest = new BatchModifyMessagesRequest
        {
            Ids = request.Select(a => a.Item.Id.ToString()).ToList(),
            AddLabelIds = addLabelIds.ToList(),
            RemoveLabelIds = removeLabelIds.ToList()
        };

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

        // Local draft mapping header must never leak to recipients.
        singleDraftRequest.Request.Mime.Headers.Remove(Domain.Constants.WinoLocalDraftHeader);

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
        if (string.IsNullOrWhiteSpace(queryText))
            return [];

        static bool IsArchiveFolder(IMailItemFolder folder)
            => folder?.SpecialFolderType == SpecialFolderType.Archive || folder?.RemoteFolderId == ServiceConstants.ARCHIVE_LABEL_ID;

        var distinctFolders = folders?
            .Where(folder => folder != null)
            .GroupBy(folder => folder.Id)
            .Select(group => group.First())
            .ToList();

        var messageIds = new HashSet<string>(StringComparer.Ordinal);

        async Task CollectMessageIdsAsync(UsersResource.MessagesResource.ListRequest request)
        {
            string pageToken = null;

            do
            {
                if (!string.IsNullOrEmpty(pageToken))
                {
                    request.PageToken = pageToken;
                }

                var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (response.Messages == null || response.Messages.Count == 0) break;

                foreach (var message in response.Messages)
                {
                    if (!string.IsNullOrEmpty(message.Id))
                    {
                        messageIds.Add(message.Id);
                    }
                }

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));
        }

        bool hasScopedQuery = queryText.StartsWith("label:", StringComparison.OrdinalIgnoreCase) ||
                              queryText.StartsWith("in:", StringComparison.OrdinalIgnoreCase);

        if (hasScopedQuery || distinctFolders?.Count == 0)
        {
            var request = _gmailService.Users.Messages.List("me");
            request.Q = queryText;
            request.MaxResults = 500;

            await CollectMessageIdsAsync(request).ConfigureAwait(false);
        }
        else
        {
            foreach (var folder in distinctFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = _gmailService.Users.Messages.List("me");
                request.MaxResults = 500;

                if (IsArchiveFolder(folder))
                {
                    // Gmail archive is virtual. Query via search operator instead of label id.
                    request.Q = $"in:archive {queryText}".Trim();
                }
                else
                {
                    request.Q = queryText;
                    request.LabelIds = new List<string> { folder.RemoteFolderId };
                }

                await CollectMessageIdsAsync(request).ConfigureAwait(false);
            }
        }

        if (messageIds.Count == 0)
            return [];

        var messageIdList = messageIds.ToList();

        // Do not download messages that already exist locally.
        var existingMessageIds = await _gmailChangeProcessor.AreMailsExistsAsync(messageIdList).ConfigureAwait(false);
        var messagesToDownload = messageIdList.Except(existingMessageIds, StringComparer.Ordinal);

        // Download missing messages in batch with metadata only.
        await DownloadMessagesInBatchAsync(messagesToDownload, cancellationToken).ConfigureAwait(false);

        // Get results from database and return.
        return await _gmailChangeProcessor.GetMailCopiesAsync(messageIdList).ConfigureAwait(false);
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
                    // Create mail packages from metadata/raw.
                    // If Gmail response is Raw format, CreateNewMailPackagesAsync will parse MIME and
                    // include it in package(s) so it can be saved to disk.
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
        try
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
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.Warning("Gmail message {MailId} not found (404) during MIME download. Deleting locally.", mailItem.Id);
            await _gmailChangeProcessor.DeleteMailAsync(Account.Id, mailItem.Id).ConfigureAwait(false);
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

    public override List<IRequestBundle<IClientServiceRequest>> DeleteFolder(DeleteFolderRequest request)
    {
        var networkCall = _gmailService.Users.Labels.Delete("me", request.Folder.RemoteFolderId);
        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> CreateSubFolder(CreateSubFolderRequest request)
    {
        var parentLabelName = request.Folder.FolderName;

        try
        {
            var parentLabel = _gmailService.Users.Labels.Get("me", request.Folder.RemoteFolderId).Execute();
            if (!string.IsNullOrWhiteSpace(parentLabel?.Name))
            {
                parentLabelName = parentLabel.Name;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to resolve full parent label name for {FolderId}. Falling back to local folder name.", request.Folder.RemoteFolderId);
        }

        var label = new Label()
        {
            Name = $"{parentLabelName}/{request.NewFolderName}"
        };

        var networkCall = _gmailService.Users.Labels.Create(label, "me");
        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> CreateRootFolder(CreateRootFolderRequest request)
    {
        var label = new Label()
        {
            Name = request.NewFolderName
        };

        var networkCall = _gmailService.Users.Labels.Create(label, "me");
        return [new HttpRequestBundle<IClientServiceRequest>(networkCall, request, request)];
    }

    #endregion

    #region Request Execution

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<IClientServiceRequest>> batchedRequests,
                                                          CancellationToken cancellationToken = default)
    {
        // First apply all UI changes immediately before any batching.
        // This ensures UI reflects changes right away, regardless of batch processing.
        foreach (var bundle in batchedRequests)
        {
            bundle.UIChangeRequest?.ApplyUIChanges();
        }

        // Batch requests per Google service instance. Calendar requests must be queued against
        // CalendarService, otherwise Gmail's batch endpoint will reject Calendar REST paths.
        var requestGroups = batchedRequests.GroupBy(bundle => bundle.NativeRequest.Service);

        foreach (var requestGroup in requestGroups)
        {
            var batchedBundles = requestGroup.Batch((int)MaximumAllowedBatchRequestSize);

            foreach (var bundle in batchedBundles)
            {
                var nativeBatchRequest = new BatchRequest(requestGroup.Key);
                var bundleTasks = new List<Task>();

                foreach (var requestBundle in bundle)
                {
                    // UI changes are already applied above before batching.
                    nativeBatchRequest.Queue<object>(requestBundle.NativeRequest, (content, error, index, message)
                        => bundleTasks.Add(ProcessSingleNativeRequestResponseAsync(requestBundle, error, message, cancellationToken)));
                }

                await nativeBatchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(bundleTasks).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessGmailRequestErrorAsync(RequestError error, IRequestBundle<IClientServiceRequest> bundle)
    {
        if (error == null) return;

        var isEntityNotFound = IsKnownGmailEntityNotFoundError(error, bundle);

        // Create error context
        var errorContext = new SynchronizerErrorContext
        {
            Account = Account,
            ErrorCode = error.Code,
            ErrorMessage = error.Message,
            RequestBundle = bundle,
            Request = bundle.Request,
            IsEntityNotFound = isEntityNotFound,
            AdditionalData = new Dictionary<string, object>
            {
                { "Error", error }
            }
        };

        // Try to handle the error with registered handlers
        var handled = await _gmailSynchronizerErrorHandlerFactory.HandleErrorAsync(errorContext);

        if (handled)
        {
            if (ShouldRevertOptimisticMailStateChange(bundle?.UIChangeRequest))
            {
                bundle?.UIChangeRequest?.RevertUIChanges();
            }

            return;
        }

        // If not handled by any specific handler, apply default error handling
        if (!handled)
        {
            CaptureSynchronizationIssue(errorContext);

            // OutOfMemoryException is a known bug in Gmail SDK.
            if (error.Code == 0)
            {
                bundle?.UIChangeRequest?.RevertUIChanges();
                throw new OutOfMemoryException(error.Message);
            }

            // Entity not found.
            if (isEntityNotFound)
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

    private static bool IsKnownGmailEntityNotFoundError(
        RequestError error,
        IRequestBundle<IClientServiceRequest> bundle)
    {
        if (error?.Code != 404 || bundle?.UIChangeRequest == null)
            return false;

        if (!IsExistingEntityOperation(bundle.UIChangeRequest))
            return false;

        var message = error.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalizedMessage = message.ToLowerInvariant();
        return normalizedMessage.Contains("requested entity")
               || normalizedMessage.Contains("message not found")
               || normalizedMessage.Contains("thread not found")
               || normalizedMessage.Contains("draft not found")
               || normalizedMessage.Contains("label not found")
               || normalizedMessage.Contains("event not found")
               || normalizedMessage.Contains("calendar not found");
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
           || request is RenameFolderRequest
           || request is DeleteFolderRequest
           || request is AcceptEventRequest
           || request is DeclineEventRequest
           || request is OutlookDeclineEventRequest
           || request is TentativeEventRequest
           || request is UpdateCalendarEventRequest
           || request is DeleteCalendarEventRequest;

    private static bool ShouldRevertOptimisticMailStateChange(IUIChangeRequest request)
        => request is BatchMarkReadRequest
        || request is MarkReadRequest
        || request is BatchChangeJunkStateRequest
        || request is ChangeJunkStateRequest
        || request is BatchChangeFlagRequest
        || request is ChangeFlagRequest;

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
        if (error != null)
        {
            await ProcessGmailRequestErrorAsync(error, bundle).ConfigureAwait(false);
            return;
        }

        await PersistSuccessfulMailStateChangesAsync(bundle).ConfigureAwait(false);

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
        else if (bundle is HttpRequestBundle<IClientServiceRequest, Event> eventBundle && eventBundle.Request is CreateCalendarEventRequest createCalendarEventRequest)
        {
            var createdEvent = await eventBundle.DeserializeBundleAsync(httpResponseMessage, GmailSynchronizerJsonContext.Default.Event, cancellationToken).ConfigureAwait(false);

            if (createdEvent == null || string.IsNullOrWhiteSpace(createdEvent.Id))
                return;

            await UploadCalendarEventAttachmentsAsync(createCalendarEventRequest, createdEvent, cancellationToken).ConfigureAwait(false);
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

    private async Task PersistSuccessfulMailStateChangesAsync(IRequestBundle<IClientServiceRequest> bundle)
    {
        switch (bundle.UIChangeRequest)
        {
            case BatchMarkReadRequest batchMarkReadRequest:
                foreach (var request in batchMarkReadRequest)
                {
                    await _gmailChangeProcessor.ChangeMailReadStatusAsync(request.Item.Id, request.IsRead).ConfigureAwait(false);
                }
                break;

            case MarkReadRequest markReadRequest:
                await _gmailChangeProcessor.ChangeMailReadStatusAsync(markReadRequest.Item.Id, markReadRequest.IsRead).ConfigureAwait(false);
                break;

            case BatchChangeFlagRequest batchChangeFlagRequest:
                foreach (var request in batchChangeFlagRequest)
                {
                    await _gmailChangeProcessor.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
                }
                break;

            case ChangeFlagRequest changeFlagRequest:
                await _gmailChangeProcessor.ChangeFlagStatusAsync(changeFlagRequest.Item.Id, changeFlagRequest.IsFlagged).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Gmail Archive is a special folder that is not visible in the Gmail web interface.
    /// We need to handle it separately.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task MapArchivedMailsAsync(CancellationToken cancellationToken)
    {
        if (!archiveFolderId.HasValue) return;

        var request = _gmailService.Users.Messages.List("me");
        request.Q = "in:archive";
        request.MaxResults = 500;

        string pageToken = null;

        var archivedMessageIds = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            if (!string.IsNullOrEmpty(pageToken)) request.PageToken = pageToken;

            var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (response.Messages == null) break;

            foreach (var message in response.Messages)
            {
                if (!string.IsNullOrEmpty(message.Id))
                {
                    archivedMessageIds.Add(message.Id);
                }
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        var result = await _gmailChangeProcessor.GetGmailArchiveComparisonResultAsync(archiveFolderId.Value, archivedMessageIds.ToList()).ConfigureAwait(false);

        var addedArchiveIds = result.Added.Distinct(StringComparer.Ordinal).ToList();
        var removedArchiveIds = result.Removed.Distinct(StringComparer.Ordinal).ToList();

        if (addedArchiveIds.Count > 0)
        {
            // Archive sync can surface messages that were never downloaded before.
            // Download metadata first so assignment creation can succeed.
            var existingBeforeDownload = await _gmailChangeProcessor.AreMailsExistsAsync(addedArchiveIds).ConfigureAwait(false);
            var missingArchiveIds = addedArchiveIds.Except(existingBeforeDownload, StringComparer.Ordinal).ToList();

            if (missingArchiveIds.Count > 0)
            {
                await DownloadMessagesInBatchAsync(missingArchiveIds, cancellationToken).ConfigureAwait(false);
            }

            var existingAfterDownload = await _gmailChangeProcessor.AreMailsExistsAsync(addedArchiveIds).ConfigureAwait(false);

            foreach (var archiveAddedItem in existingAfterDownload)
            {
                await HandleArchiveAssignmentAsync(archiveAddedItem).ConfigureAwait(false);
            }
        }

        foreach (var unAarchivedRemovedItem in removedArchiveIds)
        {
            await HandleUnarchiveAssignmentAsync(unAarchivedRemovedItem).ConfigureAwait(false);
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
            IsReadReceiptRequested = HasReadReceiptRequest(gmailMessage.Payload?.Headers),
            IsFlagged = isFlagged,
            IsFocused = isFocused,
            InReplyTo = MailHeaderExtensions.StripAngleBrackets(gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase))?.Value),
            MessageId = MailHeaderExtensions.StripAngleBrackets(gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("Message-Id", StringComparison.OrdinalIgnoreCase))?.Value),
            References = MailHeaderExtensions.NormalizeReferences(gmailMessage.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals("References", StringComparison.OrdinalIgnoreCase))?.Value),
            FileId = Guid.NewGuid(),
            ItemType = itemType
        };

        // Note: DraftId is NOT set here. Gmail's Draft resource ID is separate from ThreadId
        // and can only be obtained from the Drafts API (not Messages API).
        // DraftId is populated by:
        // - MapLocalDraftAsync (for Wino-created drafts, from CreateDraft response)
        // - MapDraftIdsAsync (for all drafts, from Drafts.List API)

        return Task.FromResult(copy);
    }

    /// <summary>
    /// Enriches a MailCopy with fields extracted from a parsed MimeMessage.
    /// This is needed when messages are downloaded with Raw format (delta sync),
    /// because the Gmail API does not populate Payload.Headers in Raw format.
    /// Fields already populated (non-null/non-empty) are NOT overwritten.
    /// </summary>
    private static void EnrichMailCopyFromMime(MailCopy copy, MimeMessage mime)
    {
        if (copy == null || mime == null) return;

        if (string.IsNullOrEmpty(copy.Subject))
            copy.Subject = mime.Subject ?? string.Empty;

        if (string.IsNullOrEmpty(copy.FromName))
        {
            var from = mime.From.Mailboxes.FirstOrDefault();
            if (from != null)
                copy.FromName = from.Name ?? string.Empty;
        }

        if (string.IsNullOrEmpty(copy.FromAddress))
        {
            var from = mime.From.Mailboxes.FirstOrDefault();
            if (from != null)
                copy.FromAddress = from.Address ?? string.Empty;
        }

        if (string.IsNullOrEmpty(copy.MessageId))
            copy.MessageId = MailHeaderExtensions.NormalizeMessageId(mime.Headers[HeaderId.MessageId]);

        if (!copy.IsReadReceiptRequested)
            copy.IsReadReceiptRequested = mime.HasReadReceiptRequest();

        if (string.IsNullOrEmpty(copy.InReplyTo))
            copy.InReplyTo = MailHeaderExtensions.NormalizeMessageId(mime.InReplyTo);

        if (string.IsNullOrEmpty(copy.References) && mime.References?.Count > 0)
            copy.References = MailHeaderExtensions.JoinStoredReferences(mime.References);

        if (!copy.HasAttachments && mime.Attachments.Any())
            copy.HasAttachments = true;

        if (copy.Importance == MailImportance.Normal)
        {
            copy.Importance = mime.Importance switch
            {
                MessageImportance.High => MailImportance.High,
                MessageImportance.Low => MailImportance.Low,
                _ => MailImportance.Normal
            };
        }
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

    private static bool HasReadReceiptRequest(IList<MessagePartHeader> headers)
        => headers?.Any(h => h.Name.Equals(Domain.Constants.DispositionNotificationToHeader, StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(h.Value)) == true;

    private static bool LooksLikeReadReceipt(IList<MessagePartHeader> headers)
    {
        var contentType = headers?.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        return !string.IsNullOrWhiteSpace(contentType)
               && contentType.Contains("disposition-notification", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AccountContact> ExtractContactsFromGmailMessage(Message message, MimeMessage mimeMessage)
    {
        var contacts = new Dictionary<string, AccountContact>(StringComparer.OrdinalIgnoreCase);

        AddFromHeaders(message?.Payload?.Headers);

        if (mimeMessage != null)
        {
            AddFromInternetAddressList(mimeMessage.From);
            AddFromInternetAddressList(mimeMessage.To);
            AddFromInternetAddressList(mimeMessage.Cc);
            AddFromInternetAddressList(mimeMessage.Bcc);
            AddFromInternetAddressList(mimeMessage.ReplyTo);

            if (mimeMessage.Sender is MailboxAddress senderMailbox)
            {
                AddContact(senderMailbox.Address, senderMailbox.Name);
            }
        }

        return contacts.Values.ToList();

        void AddFromHeaders(IList<MessagePartHeader> headers)
        {
            if (headers == null || headers.Count == 0) return;

            AddFromHeader("From");
            AddFromHeader("Sender");
            AddFromHeader("To");
            AddFromHeader("Cc");
            AddFromHeader("Bcc");
            AddFromHeader("Reply-To");

            void AddFromHeader(string headerName)
            {
                var headerValue = headers
                    .FirstOrDefault(h => h.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                    ?.Value;

                if (string.IsNullOrWhiteSpace(headerValue)) return;

                try
                {
                    var addresses = InternetAddressList.Parse(headerValue);
                    foreach (var mailbox in addresses.Mailboxes)
                    {
                        AddContact(mailbox.Address, mailbox.Name);
                    }
                }
                catch
                {
                    var (name, email) = ExtractNameAndEmailFromHeader(headerValue);
                    AddContact(email, name);
                }
            }
        }

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

    /// <summary>
    /// Creates new mail packages for the given message.
    /// AssignedFolder is null since the LabelId is parsed out of the Message.
    /// If Gmail Message includes Raw payload, MIME is parsed and attached to packages.
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
        MimeMessage mimeMessage = null;

        // Raw format is used in delta sync and does not populate Payload.Headers.
        // Parse MIME from Raw so we can resolve draft mapping header and persist mime content.
        if (!string.IsNullOrEmpty(message?.Raw))
        {
            try
            {
                mimeMessage = message.GetGmailMimeMessage();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to parse MIME from raw Gmail message {MessageId}", message?.Id);
            }
        }

        // Create base MailCopy from metadata only - NO MIME download
        var baseMailCopy = await CreateMinimalMailCopyAsync(message, assignedFolder, cancellationToken);

        // Initial sync metadata flow does not include MIME, but calendar invitations need MIME
        // for date rendering and invitation-to-calendar mapping.
        if (mimeMessage == null &&
            (baseMailCopy?.ItemType == MailItemType.CalendarInvitation || LooksLikeReadReceipt(message?.Payload?.Headers)) &&
            !string.IsNullOrEmpty(message?.Id))
        {
            try
            {
                var rawRequest = _gmailService.Users.Messages.Get("me", message.Id);
                rawRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;

                var rawMessage = await rawRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(rawMessage?.Raw))
                {
                    mimeMessage = rawMessage.GetGmailMimeMessage();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to fetch raw MIME for Gmail message {MessageId}", message.Id);
            }
        }

        if (mimeMessage != null)
        {
            // Raw responses don't include metadata headers. Backfill important fields from MIME.
            EnrichMailCopyFromMime(baseMailCopy, mimeMessage);
        }

        await TryMapCalendarInvitationAsync(baseMailCopy, mimeMessage, cancellationToken).ConfigureAwait(false);

        var extractedContacts = ExtractContactsFromGmailMessage(message, mimeMessage);

        // Check for local draft mapping using X-Wino-Draft-Id header.
        // For Metadata format we read from Payload.Headers.
        // For Raw format (Payload is null), we read from parsed MIME headers.
        if (baseMailCopy.IsDraft)
        {
            var draftIdHeader = message.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals(Domain.Constants.WinoLocalDraftHeader, StringComparison.OrdinalIgnoreCase))?.Value
                                ?? mimeMessage?.Headers?.FirstOrDefault(h => h.Field.Equals(Domain.Constants.WinoLocalDraftHeader, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrEmpty(draftIdHeader) && Guid.TryParse(draftIdHeader, out _))
            {
                if (Guid.TryParse(draftIdHeader, out Guid localDraftCopyUniqueId))
                {
                    // This message belongs to existing local draft copy.
                    // Map remote ids to local copy and skip creating duplicate rows.
                    bool isMappingSuccessful = await _gmailChangeProcessor.MapLocalDraftAsync(
                        Account.Id,
                        localDraftCopyUniqueId,
                        baseMailCopy.Id,
                        baseMailCopy.DraftId,
                        baseMailCopy.ThreadId).ConfigureAwait(false);

                    if (isMappingSuccessful)
                    {
                        // Keep local draft MIME in sync with the fetched remote raw MIME if available.
                        if (mimeMessage != null)
                        {
                            var mappedDraftCopies = await _gmailChangeProcessor.GetMailCopiesAsync([baseMailCopy.Id]).ConfigureAwait(false);
                            if (mappedDraftCopies != null)
                            {
                                var savedFileIds = new HashSet<Guid>();
                                foreach (var mappedCopy in mappedDraftCopies)
                                {
                                    if (mappedCopy.FileId == Guid.Empty || !savedFileIds.Add(mappedCopy.FileId))
                                        continue;

                                    await _gmailChangeProcessor.SaveMimeFileAsync(mappedCopy.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
                                }
                            }
                        }

                        return null;
                    }
                }
            }
        }

        // For Gmail, a single mail can have multiple labels (folders).
        // Each label requires a separate MailCopy entry in the database with:
        // - Same Id, UniqueId, FileId (shared across all copies)
        // - Different FolderId (one per label)
        // ARCHIVE label is excluded here as it's virtual and handled by MapArchivedMailsAsync
        if (message.LabelIds is not null)
        {
            // Generate shared identifiers that will be the same for all copies of this mail
            var sharedId = baseMailCopy.Id;
            var sharedFileId = baseMailCopy.FileId;

            foreach (var labelId in message.LabelIds)
            {
                // Skip ARCHIVE label - it's virtual and handled separately
                if (labelId == ServiceConstants.ARCHIVE_LABEL_ID)
                    continue;

                // Create a new MailCopy instance for each label to avoid shared reference issues
                var mailCopyForLabel = await CreateMinimalMailCopyAsync(message, assignedFolder, cancellationToken);

                if (mimeMessage != null)
                {
                    EnrichMailCopyFromMime(mailCopyForLabel, mimeMessage);
                }

                // Ensure all copies share the same Id and FileId
                mailCopyForLabel.Id = sharedId;
                mailCopyForLabel.FileId = sharedFileId;

                packageList.Add(new NewMailItemPackage(mailCopyForLabel, mimeMessage, labelId, extractedContacts));
            }
        }

        return packageList;
    }

    private async Task TryMapCalendarInvitationAsync(MailCopy baseMailCopy, MimeMessage mimeMessage, CancellationToken cancellationToken)
    {
        if (baseMailCopy == null || baseMailCopy.ItemType != MailItemType.CalendarInvitation || mimeMessage == null)
            return;

        var invitationUid = mimeMessage.ExtractInvitationUid();
        if (string.IsNullOrWhiteSpace(invitationUid))
            return;

        var calendars = await _gmailChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        if (calendars == null || calendars.Count == 0)
            return;

        foreach (var calendar in calendars)
        {
            try
            {
                var listRequest = _calendarService.Events.List(calendar.RemoteCalendarId);
                listRequest.ICalUID = invitationUid;
                listRequest.MaxResults = 1;
                listRequest.SingleEvents = false;

                var listResponse = await listRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                var matchedEvent = listResponse?.Items?.FirstOrDefault();
                if (matchedEvent == null || string.IsNullOrWhiteSpace(matchedEvent.Id))
                    continue;

                await _gmailChangeProcessor.ManageCalendarEventAsync(matchedEvent, calendar, Account).ConfigureAwait(false);

                var localCalendarItem = await _gmailChangeProcessor.GetCalendarItemAsync(calendar.Id, matchedEvent.Id).ConfigureAwait(false);
                if (localCalendarItem == null)
                    return;

                await _gmailChangeProcessor.UpsertMailInvitationCalendarMappingAsync(new MailInvitationCalendarMapping()
                {
                    Id = Guid.NewGuid(),
                    AccountId = Account.Id,
                    MailCopyId = baseMailCopy.Id,
                    InvitationUid = invitationUid,
                    CalendarId = calendar.Id,
                    CalendarItemId = localCalendarItem.Id,
                    CalendarRemoteEventId = matchedEvent.Id
                }).ConfigureAwait(false);

                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to map Gmail calendar invitation mail {MailCopyId} for calendar {CalendarId}", baseMailCopy.Id, calendar.Id);
            }
        }
    }

    #endregion

    #region Calendar Operations

    public override List<IRequestBundle<IClientServiceRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var calendarItem = request.PreparedItem;
        var attendees = request.PreparedEvent.Attendees;
        var reminders = request.PreparedEvent.Reminders;
        var calendar = request.AssignedCalendar;

        var googleEvent = new Event
        {
            Id = calendarItem.Id.ToString("N").ToLowerInvariant(),
            Summary = calendarItem.Title,
            Description = calendarItem.Description,
            Location = calendarItem.Location,
            Status = calendarItem.Status == CalendarItemStatus.Accepted ? "confirmed" : "tentative",
            Transparency = calendarItem.ShowAs == CalendarItemShowAs.Free ? "transparent" : "opaque"
        };

        if (calendarItem.IsAllDayEvent)
        {
            googleEvent.Start = new EventDateTime
            {
                Date = calendarItem.StartDate.ToString("yyyy-MM-dd"),
                TimeZone = NormalizeGoogleTimeZoneId(calendarItem.StartTimeZone)
            };
            googleEvent.End = new EventDateTime
            {
                Date = calendarItem.EndDate.ToString("yyyy-MM-dd"),
                TimeZone = NormalizeGoogleTimeZoneId(calendarItem.EndTimeZone)
            };
        }
        else
        {
            var startTimeZone = NormalizeGoogleTimeZoneId(calendarItem.StartTimeZone);
            var endTimeZone = NormalizeGoogleTimeZoneId(calendarItem.EndTimeZone ?? calendarItem.StartTimeZone);

            googleEvent.Start = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(calendarItem.StartDate, ResolveOffset(calendarItem.StartDate, calendarItem.StartTimeZone)),
                TimeZone = startTimeZone
            };
            googleEvent.End = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(calendarItem.EndDate, ResolveOffset(calendarItem.EndDate, calendarItem.EndTimeZone ?? calendarItem.StartTimeZone)),
                TimeZone = endTimeZone
            };
        }

        if (attendees.Count > 0)
        {
            googleEvent.Attendees = attendees.Select(a => new EventAttendee
            {
                Email = a.Email,
                DisplayName = a.Name,
                Optional = a.IsOptionalAttendee
            }).ToList();
        }

        if (reminders.Count > 0)
        {
            googleEvent.Reminders = new Event.RemindersData
            {
                UseDefault = false,
                Overrides = reminders.Select(reminder => new EventReminder
                {
                    Method = reminder.ReminderType == CalendarItemReminderType.Email ? "email" : "popup",
                    Minutes = (int)Math.Max(0, reminder.DurationInSeconds / 60)
                }).ToList()
            };
        }

        if (!string.IsNullOrWhiteSpace(calendarItem.Recurrence))
        {
            googleEvent.Recurrence = calendarItem.Recurrence
                .Split(Wino.Core.Domain.Constants.CalendarEventRecurrenceRuleSeperator, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        var insertRequest = _calendarService.Events.Insert(googleEvent, calendar.RemoteCalendarId);
        insertRequest.SendUpdates = attendees.Count > 0
            ? Google.Apis.Calendar.v3.EventsResource.InsertRequest.SendUpdatesEnum.All
            : Google.Apis.Calendar.v3.EventsResource.InsertRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IClientServiceRequest, Event>(insertRequest, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> AcceptEvent(AcceptEventRequest request)
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
        }, calendar.RemoteCalendarId, remoteEventId);

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

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
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
        }, calendar.RemoteCalendarId, remoteEventId);

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

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
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
        }, calendar.RemoteCalendarId, remoteEventId);

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

        var remoteEventId = calendarItem.RemoteEventId.GetProviderRemoteEventId();
        if (string.IsNullOrEmpty(remoteEventId))
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
        var updateRequest = _calendarService.Events.Update(googleEvent, calendar.RemoteCalendarId, remoteEventId);

        // Send notifications to attendees if the event has attendees
        updateRequest.SendUpdates = (attendees != null && attendees.Count > 0)
            ? Google.Apis.Calendar.v3.EventsResource.UpdateRequest.SendUpdatesEnum.All
            : Google.Apis.Calendar.v3.EventsResource.UpdateRequest.SendUpdatesEnum.None;

        return [new HttpRequestBundle<IClientServiceRequest>(updateRequest, request)];
    }

    public override List<IRequestBundle<IClientServiceRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        => UpdateCalendarEvent(request);

    public override List<IRequestBundle<IClientServiceRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
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

        var deleteRequest = _calendarService.Events.Delete(calendar.RemoteCalendarId, remoteEventId);

        // Send cancellation notifications to attendees
        deleteRequest.SendUpdates = Google.Apis.Calendar.v3.EventsResource.DeleteRequest.SendUpdatesEnum.All;

        return [new HttpRequestBundle<IClientServiceRequest>(deleteRequest, request)];
    }

    #endregion

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync();

        _gmailService.Dispose();
        _peopleService.Dispose();
        _calendarService.Dispose();
        _driveService.Dispose();
        _googleHttpClient.Dispose();
    }

    private async Task UploadCalendarEventAttachmentsAsync(CreateCalendarEventRequest request, Event createdEvent, CancellationToken cancellationToken)
    {
        var composeAttachments = request.ComposeResult.Attachments ?? [];
        if (composeAttachments.Count == 0)
            return;

        if (composeAttachments.Count > 25)
            throw new InvalidOperationException("Google Calendar supports at most 25 attachments per event.");

        var eventAttachments = createdEvent.Attachments?
            .Where(attachment => attachment != null && !string.IsNullOrWhiteSpace(attachment.FileUrl))
            .ToList() ?? [];

        foreach (var attachment in composeAttachments.Where(a => !string.IsNullOrWhiteSpace(a.FilePath) && File.Exists(a.FilePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventAttachments.Add(await UploadAttachmentToDriveAsync(attachment, cancellationToken).ConfigureAwait(false));
        }

        if (eventAttachments.Count == 0)
            return;

        var patchRequest = _calendarService.Events.Patch(new Event
        {
            Attachments = eventAttachments
        }, request.AssignedCalendar.RemoteCalendarId, createdEvent.Id);

        patchRequest.SupportsAttachments = true;
        patchRequest.SendUpdates = Google.Apis.Calendar.v3.EventsResource.PatchRequest.SendUpdatesEnum.None;

        await patchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<EventAttachment> UploadAttachmentToDriveAsync(
        Wino.Core.Domain.Models.Calendar.CalendarEventComposeAttachmentDraft attachment,
        CancellationToken cancellationToken)
    {
        var fileName = string.IsNullOrWhiteSpace(attachment.FileName)
            ? Path.GetFileName(attachment.FilePath)
            : attachment.FileName;
        var contentType = MimeTypes.GetMimeType(fileName);

        await using var fileStream = File.OpenRead(attachment.FilePath);

        var uploadRequest = _driveService.Files.Create(new DriveFile
        {
            Name = fileName,
            MimeType = contentType
        }, fileStream, contentType);
        uploadRequest.Fields = "id,name,mimeType,webViewLink";

        var uploadProgress = await uploadRequest.UploadAsync(cancellationToken).ConfigureAwait(false);

        if (uploadProgress.Status != UploadStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Failed to upload '{fileName}' to Google Drive. Upload status: {uploadProgress.Status}.");
        }

        var uploadedFile = uploadRequest.ResponseBody;
        if (uploadedFile == null || string.IsNullOrWhiteSpace(uploadedFile.Id) || string.IsNullOrWhiteSpace(uploadedFile.WebViewLink))
        {
            throw new InvalidOperationException($"Google Drive did not return a valid attachment link for '{fileName}'.");
        }

        return new EventAttachment
        {
            FileId = uploadedFile.Id,
            FileUrl = uploadedFile.WebViewLink,
            MimeType = uploadedFile.MimeType ?? contentType,
            Title = uploadedFile.Name ?? fileName
        };
    }

    private static TimeSpan ResolveOffset(DateTime dateTime, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeSpan.Zero;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(dateTime);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static string NormalizeGoogleTimeZoneId(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return timeZoneId;

        if (timeZoneId.Contains('/'))
            return timeZoneId;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaTimeZoneId))
            return ianaTimeZoneId;

        return timeZoneId;
    }
}

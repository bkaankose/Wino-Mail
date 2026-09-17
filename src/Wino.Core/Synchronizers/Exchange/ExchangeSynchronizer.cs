using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Exchange.WebServices.Data;
using MimeKit;
using Serilog;
using Wino.Authentication.Exchange;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Rules;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Extensions;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Contact;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Tasks;
using Wino.Messaging.UI;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task and expose the EWS one as EwsTask.
using Task = System.Threading.Tasks.Task;
using EwsTask = Microsoft.Exchange.WebServices.Data.Task;
using EwsTaskStatus = Microsoft.Exchange.WebServices.Data.TaskStatus;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Synchronizes on-premises Exchange accounts over Exchange Web Services. This is the fallback
/// transport for servers that do not offer MAPI/HTTP (before Exchange 2013 SP1, or with the
/// protocol disabled) and for accounts the user pins to EWS; the MAPI synchronizer derives from
/// it and replaces every surface: mail, calendar (CalendarView expands series server-side, occurrences
/// are stored flat), contacts and tasks.
/// </summary>
public class ExchangeSynchronizer : WinoSynchronizer<EwsRequest, Item, Appointment, AccountContact>
{
    public override uint BatchModificationSize => 100;
    public override uint InitialMessageDownloadCountPerFolder => 500;

    // Exchange2013_SP1 is the highest schema the EWS Managed API exposes; it negotiates
    // up against 2016/2019/SE. TODO: make per-account configurable for older servers.
    private const ExchangeVersion TargetExchangeVersion = ExchangeVersion.Exchange2013_SP1;

    private readonly ILogger _logger = Log.ForContext<ExchangeSynchronizer>();
    private readonly IExchangeAuthenticator _exchangeAuthenticator;
    private readonly IExchangeChangeProcessor _exchangeChangeProcessor;
    private readonly IExchangeSynchronizerErrorHandlerFactory _errorHandlerFactory;
    private readonly IContactService _contactService;
    private readonly IContactPictureFileService _contactPictureFileService;
    private readonly ITaskService _taskService;
    private readonly LocalContactSynchronizer _localContactSynchronizer;
    private readonly LocalTaskSynchronizer _localTaskSynchronizer;

    // Seams for the MAPI subclass (Synchronizers/Mapi), which replaces this class one surface at a time.
    protected IExchangeAuthenticator ExchangeAuthenticator => _exchangeAuthenticator;
    protected IExchangeChangeProcessor ExchangeChangeProcessor => _exchangeChangeProcessor;
    protected IExchangeSynchronizerErrorHandlerFactory ErrorHandlerFactory => _errorHandlerFactory;
    protected IContactService ContactService => _contactService;
    protected IContactPictureFileService ContactPictureFileService => _contactPictureFileService;
    protected ITaskService TaskService => _taskService;

    public ExchangeSynchronizer(MailAccount account,
                                IExchangeAuthenticator exchangeAuthenticator,
                                IExchangeChangeProcessor exchangeChangeProcessor,
                                IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory,
                                IContactService contactService = null,
                                IContactPictureFileService contactPictureFileService = null,
                                ITaskService taskService = null)
        : base(account, WeakReferenceMessenger.Default)
    {
        _exchangeAuthenticator = exchangeAuthenticator;
        _exchangeChangeProcessor = exchangeChangeProcessor;
        _errorHandlerFactory = errorHandlerFactory;
        _contactService = contactService;
        _contactPictureFileService = contactPictureFileService;
        _taskService = taskService;
        _localContactSynchronizer = new LocalContactSynchronizer(contactService, contactPictureFileService);
        _localTaskSynchronizer = new LocalTaskSynchronizer(taskService);
    }

    // EWS throws when reading properties not requested here.
    private static readonly PropertySet ItemMetadataPropertySet = new(
        BasePropertySet.IdOnly,
        ItemSchema.Subject,
        ItemSchema.DateTimeReceived,
        ItemSchema.Size,
        ItemSchema.HasAttachments,
        ItemSchema.Importance,
        ItemSchema.ConversationId,
        EmailMessageSchema.From,
        EmailMessageSchema.IsRead,
        ItemSchema.Flag,
        ItemSchema.ItemClass,
        EmailMessageSchema.InternetMessageId);

    protected async Task<ExchangeService> CreateServiceAsync(TimeZoneInfo timeZone = null)
    {
        var serverInformation = Account.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        var credentials = await _exchangeAuthenticator.GetCredentialsAsync(Account).ConfigureAwait(false);

        _logger.Debug(
            "Building EWS service. Url={Url}, OAuth={UseOAuth}, ConfiguredUser={User}, CredentialType={CredType}",
            serverInformation.IncomingServer,
            serverInformation.UseOAuthAuthentication,
            serverInformation.IncomingServerUsername,
            credentials?.GetType().Name);

        var service = timeZone == null
            ? new ExchangeService(TargetExchangeVersion)
            {
                Credentials = credentials,
                Url = new Uri(serverInformation.IncomingServer)
            }
            : new ExchangeService(TargetExchangeVersion, timeZone)
            {
                Credentials = credentials,
                Url = new Uri(serverInformation.IncomingServer)
            };

        // Route requests to the owning mailbox (Microsoft-recommended). Multi-CAS/DAG deployments can
        // otherwise proxy to a non-owning server; the header keeps every request on the mailbox's backend.
        if (!string.IsNullOrEmpty(Account.Address))
            service.HttpHeaders["X-AnchorMailbox"] = Account.Address;

        return service;
    }

    public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(Item message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        var mailCopy = MapToMailCopy(message, assignedFolder);
        if (mailCopy == null)
            return Task.FromResult<List<NewMailItemPackage>>(null);

        var package = new NewMailItemPackage(mailCopy, null, assignedFolder.RemoteFolderId);
        return Task.FromResult<List<NewMailItemPackage>>([package]);
    }

    /// <summary>
    /// Best-effort proxy-address discovery. EWS has no direct "my proxy addresses" call,
    /// so this resolves the mailbox's directory entry and keeps the root alias as a fallback.
    /// </summary>
    protected override async Task SynchronizeAliasesAsync()
    {
        var aliases = new Dictionary<string, RemoteAccountAlias>(StringComparer.OrdinalIgnoreCase);

        void AddAlias(string rawAddress)
        {
            var normalized = CleanSmtpAddress(rawAddress);
            if (normalized == null)
                return;

            var isAccountAddress = normalized.Equals(Account.Address, StringComparison.OrdinalIgnoreCase);

            if (aliases.TryGetValue(normalized, out var existing))
            {
                existing.IsPrimary |= isAccountAddress;
                existing.IsRootAlias |= isAccountAddress;
                return;
            }

            aliases[normalized] = new RemoteAccountAlias
            {
                AliasAddress = normalized,
                ReplyToAddress = normalized,
                IsPrimary = isAccountAddress,
                IsRootAlias = isAccountAddress,
                IsVerified = true,
                Source = AliasSource.ProviderDiscovered,
                SendCapability = AliasSendCapability.Confirmed
            };
        }

        AddAlias(Account.Address);

        try
        {
            var service = await CreateServiceAsync().ConfigureAwait(false);

            var resolutions = await service
                .ResolveName(Account.Address, ResolveNameSearchLocation.DirectoryOnly, returnContactDetails: true, CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var resolution in resolutions)
            {
                // ResolveName can be ambiguous; only use the entry for this mailbox.
                if (!string.Equals(CleanSmtpAddress(resolution.Mailbox?.Address), Account.Address, StringComparison.OrdinalIgnoreCase))
                    continue;

                AddAlias(resolution.Mailbox?.Address);

                var contact = resolution.Contact;
                if (contact?.EmailAddresses == null)
                    continue;

                foreach (var key in new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 })
                {
                    if (contact.EmailAddresses.TryGetValue(key, out var emailAddress))
                        AddAlias(emailAddress?.Address);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Exchange alias (proxy-address) discovery via ResolveName failed for {Account}.", Account.Name);
        }

        await _exchangeChangeProcessor
            .UpdateRemoteAliasInformationAsync(Account, aliases.Values.ToList())
            .ConfigureAwait(false);
    }

    // Normalizes an EWS address to a usable SMTP address, or null. Strips an "SMTP:" prefix and rejects
    // non-SMTP routing (EX:, X500:, EUM:) and anything that isn't a plausible e-mail.
    private static string CleanSmtpAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var normalized = address.Trim();

        var colonIndex = normalized.IndexOf(':');
        if (colonIndex >= 0)
        {
            if (!normalized[..colonIndex].Equals("SMTP", StringComparison.OrdinalIgnoreCase))
                return null; // EX:, X500:, EUM:, etc.
            normalized = normalized[(colonIndex + 1)..].Trim();
        }

        return normalized.Contains('@') && normalized.Contains('.') ? normalized : null;
    }

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);

        if (options.Type is MailSynchronizationType.FullFolders or MailSynchronizationType.FoldersOnly)
            await SynchronizeFoldersAsync(service, cancellationToken).ConfigureAwait(false);

        if (options.Type == MailSynchronizationType.FoldersOnly)
            return MailSynchronizationResult.Empty;

        var foldersToSync = (await _exchangeChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false))
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .ToList();

        var downloaded = new List<MailCopy>();
        foreach (var folder in foldersToSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                downloaded.AddRange(await SynchronizeFolderItemsAsync(service, folder, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ServiceResponseException srex) when (
                srex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound or ServiceError.ErrorNonExistentMailbox
                || srex.Message.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
            {
                // The folder was deleted on the server (e.g. from OWA). Drop it locally so it stops failing
                // every sync and disappears from the tree, then refresh the navigation.
                _logger.Warning("Exchange folder {Folder} ({RemoteId}) no longer exists on the server; removing locally.",
                    folder.FolderName, folder.RemoteFolderId);
                await _exchangeChangeProcessor.DeleteFolderAsync(Account.Id, folder.RemoteFolderId).ConfigureAwait(false);
                WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
            }
            catch (Exception ex)
            {
                var errorContext = new SynchronizerErrorContext
                {
                    Account = Account,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    FolderId = folder.Id,
                    FolderName = folder.FolderName,
                    OperationType = "ExchangeFolderSync"
                };

                await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                CaptureSynchronizationIssue(errorContext);
                _logger.Error(ex, "Exchange folder sync failed for {Folder} ({Account}).", folder.FolderName, Account.Name);
            }
        }

        return MailSynchronizationResult.Completed(downloaded);
    }

    #region Calendar

    protected const int CalendarWindowPastMonths = 3;
    protected const int CalendarWindowFutureMonths = 12;

    /// <summary>
    /// The account calendars that take part in this pass: every enabled one, or only the ones a
    /// <c>SingleCalendar</c> request names (a push notification for one calendar folder).
    /// </summary>
    protected async Task<List<AccountCalendar>> GetCalendarsToSynchronizeAsync(CalendarSynchronizationOptions options)
    {
        var calendars = (await _exchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false))
            .Where(c => c.IsSynchronizationEnabled)
            .ToList();

        if (options?.Type == CalendarSynchronizationType.SingleCalendar && options.SynchronizationCalendarIds is { Count: > 0 } ids)
            calendars = calendars.Where(c => ids.Contains(c.Id)).ToList();

        return calendars;
    }

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
            return CalendarSynchronizationResult.Empty;

        if (Account.CalendarIntegrationSource != AccountIntegrationSource.Provider || !Account.IsCalendarAccessGranted)
            return CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable));

        try
        {
            var service = await CreateServiceAsync(TimeZoneInfo.Utc).ConfigureAwait(false);

            await SynchronizeCalendarsAsync(service, cancellationToken).ConfigureAwait(false);

            // The periodic loop only asks for calendar metadata; Exchange has no server-side change feed
            // for events outside push, and the window read is cheap on a kept session, so every pass
            // refreshes events as well. Otherwise events would arrive only on a manual sync.

            var windowStartUtc = DateTime.UtcNow.AddMonths(-CalendarWindowPastMonths);
            var windowEndUtc = DateTime.UtcNow.AddMonths(CalendarWindowFutureMonths);

            foreach (var calendar in await GetCalendarsToSynchronizeAsync(options).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SynchronizeCalendarEventsForCalendarAsync(service, calendar, windowStartUtc, windowEndUtc, cancellationToken).ConfigureAwait(false);
            }

            return CalendarSynchronizationResult.Empty;
        }
        catch (OperationCanceledException)
        {
            return CalendarSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "EWS calendar synchronization failed for {Address}.", Account.Address);
            return CalendarSynchronizationResult.Failed(ex);
        }
    }

    private async Task SynchronizeCalendarsAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var properties = new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass);

        var defaultCalendar = await CalendarFolder.Bind(service, WellKnownFolderName.Calendar, properties).ConfigureAwait(false);

        var view = new FolderView(1000) { Traversal = FolderTraversal.Deep, PropertySet = properties };
        var appointmentFolders = await service
            .FindFolders(WellKnownFolderName.MsgFolderRoot, new SearchFilter.IsEqualTo(FolderSchema.FolderClass, "IPF.Appointment"), view)
            .ConfigureAwait(false);

        var remoteFolders = new Dictionary<string, Folder> { [defaultCalendar.Id.UniqueId] = defaultCalendar };
        foreach (var folder in appointmentFolders.Folders)
            remoteFolders[folder.Id.UniqueId] = folder;

        var localCalendars = await _exchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        foreach (var local in localCalendars)
        {
            if (!remoteFolders.ContainsKey(local.RemoteCalendarId))
                await _exchangeChangeProcessor.DeleteAccountCalendarAsync(local).ConfigureAwait(false);
        }

        foreach (var (remoteId, folder) in remoteFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isPrimary = remoteId == defaultCalendar.Id.UniqueId;
            var name = string.IsNullOrWhiteSpace(folder.DisplayName) ? "Calendar" : folder.DisplayName;

            var existing = localCalendars.FirstOrDefault(c => c.RemoteCalendarId == remoteId);
            if (existing == null)
            {
                await _exchangeChangeProcessor.InsertAccountCalendarAsync(BuildAccountCalendar(remoteId, name, isPrimary)).ConfigureAwait(false);
            }
            else if (existing.Name != name || existing.IsPrimary != isPrimary)
            {
                existing.Name = name;
                existing.IsPrimary = isPrimary;
                await _exchangeChangeProcessor.UpdateAccountCalendarAsync(existing).ConfigureAwait(false);
            }
        }
    }

    protected AccountCalendar BuildAccountCalendar(string remoteCalendarId, string name, bool isPrimary)
        => new()
        {
            Id = Guid.NewGuid(),
            AccountId = Account.Id,
            RemoteCalendarId = remoteCalendarId,
            Name = name,
            IsPrimary = isPrimary,
            IsSynchronizationEnabled = true,
            IsReadOnly = false,
            DefaultShowAs = CalendarItemShowAs.Busy,
            TextColorHex = "#FFFFFF",
            BackgroundColorHex = string.IsNullOrWhiteSpace(Account.AccountColorHex) ? "#2564CF" : Account.AccountColorHex,
            TimeZone = TimeZoneInfo.Local.Id
        };

    private static readonly PropertySet EventPropertySet = new(
        BasePropertySet.IdOnly,
        ItemSchema.Subject, ItemSchema.Body, ItemSchema.Sensitivity,
        ItemSchema.DateTimeCreated, ItemSchema.LastModifiedTime,
        AppointmentSchema.Start, AppointmentSchema.End, AppointmentSchema.Location,
        AppointmentSchema.Organizer, AppointmentSchema.LegacyFreeBusyStatus,
        AppointmentSchema.IsReminderSet, AppointmentSchema.ReminderMinutesBeforeStart,
        AppointmentSchema.RequiredAttendees, AppointmentSchema.OptionalAttendees,
        AppointmentSchema.MyResponseType, AppointmentSchema.IsAllDayEvent,
        AppointmentSchema.StartTimeZone, AppointmentSchema.EndTimeZone,
        ExchangeCalendarSchema.WinoClientTrackingId)
    { RequestedBodyType = BodyType.HTML };

    private async Task SynchronizeCalendarEventsForCalendarAsync(ExchangeService service, AccountCalendar calendar, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
    {
        var calendarFolder = await CalendarFolder
            .Bind(service, new FolderId(calendar.RemoteCalendarId), new PropertySet(BasePropertySet.IdOnly))
            .ConfigureAwait(false);

        var seenRemoteIds = new HashSet<string>();

        // Skip deletion reconciliation if any chunk hits the server result cap.
        var windowComplete = true;

        var chunkStartUtc = windowStartUtc;
        while (chunkStartUtc < windowEndUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkEndUtc = chunkStartUtc.AddMonths(3);
            if (chunkEndUtc > windowEndUtc)
                chunkEndUtc = windowEndUtc;

            var view = new CalendarView(chunkStartUtc, chunkEndUtc, 1000) { PropertySet = new PropertySet(BasePropertySet.IdOnly) };
            var results = await calendarFolder.FindAppointments(view).ConfigureAwait(false);
            var appointments = results.Items;

            if (appointments.Count > 0)
            {
                await service.LoadPropertiesForItems(appointments, EventPropertySet).ConfigureAwait(false);

                foreach (var appointment in appointments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    seenRemoteIds.Add(appointment.Id.UniqueId);
                    await _exchangeChangeProcessor.ManageCalendarEventAsync(MapToSyncedEvent(appointment), calendar, Account).ConfigureAwait(false);
                }
            }

            if (results.MoreAvailable)
            {
                windowComplete = false;
                _logger.Warning("Calendar window chunk for {Calendar} hit the result cap; skipping deletion reconciliation this pass to avoid removing valid events.", calendar.Name);
            }

            chunkStartUtc = chunkEndUtc;
        }

        if (!windowComplete)
            return;

        var localEvents = await _exchangeChangeProcessor.GetCalendarItemsInRangeAsync(calendar, windowStartUtc, windowEndUtc).ConfigureAwait(false);
        foreach (var local in localEvents)
        {
            if (!string.IsNullOrEmpty(local.RemoteEventId) &&
                !seenRemoteIds.Contains(local.RemoteEventId.GetProviderRemoteEventId()))
            {
                await _exchangeChangeProcessor.DeleteCalendarItemAsync(local.Id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>An EWS appointment (one occurrence; CalendarView expands series server-side) in the neutral shape.</summary>
    private static SyncedCalendarEvent MapToSyncedEvent(Appointment appointment)
    {
        Guid? clientTrackingId = null;
        if (appointment.TryGetProperty(ExchangeCalendarSchema.WinoClientTrackingId, out string trackingValue) &&
            Guid.TryParseExact(trackingValue, "N", out var parsedTrackingId))
        {
            clientTrackingId = parsedTrackingId;
        }

        var startUtc = DateTime.SpecifyKind(appointment.Start, DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(appointment.End, DateTimeKind.Utc);

        string ianaTimeZone = null;
        try
        {
            if (appointment.StartTimeZone != null && TimeZoneInfo.TryConvertWindowsIdToIanaId(appointment.StartTimeZone.Id, out var iana))
                ianaTimeZone = iana;
        }
        catch
        {
        }

        return new SyncedCalendarEvent
        {
            RemoteId = appointment.Id.UniqueId.WithClientTrackingId(clientTrackingId),
            Title = appointment.Subject,
            Description = SafeGet(() => appointment.Body?.Text),
            Location = appointment.Location,
            StartUtc = startUtc,
            EndUtc = endUtc,
            TimeZoneIana = ianaTimeZone,
            IsAllDay = SafeGet(() => (bool?)appointment.IsAllDayEvent) ?? false,
            OrganizerEmail = appointment.Organizer?.Address,
            OrganizerName = appointment.Organizer?.Name,
            CreatedAt = appointment.DateTimeCreated,
            UpdatedAt = appointment.LastModifiedTime,
            Visibility = appointment.Sensitivity switch
            {
                Sensitivity.Personal or Sensitivity.Private => CalendarItemVisibility.Private,
                Sensitivity.Confidential => CalendarItemVisibility.Confidential,
                _ => CalendarItemVisibility.Public
            },
            ShowAs = appointment.LegacyFreeBusyStatus switch
            {
                LegacyFreeBusyStatus.Free => CalendarItemShowAs.Free,
                LegacyFreeBusyStatus.Tentative => CalendarItemShowAs.Tentative,
                LegacyFreeBusyStatus.OOF => CalendarItemShowAs.OutOfOffice,
                LegacyFreeBusyStatus.WorkingElsewhere => CalendarItemShowAs.WorkingElsewhere,
                _ => CalendarItemShowAs.Busy
            },
            MyResponse = appointment.MyResponseType switch
            {
                MeetingResponseType.Tentative => CalendarItemStatus.Tentative,
                MeetingResponseType.Accept => CalendarItemStatus.Accepted,
                MeetingResponseType.Organizer => CalendarItemStatus.Accepted,
                MeetingResponseType.Decline => CalendarItemStatus.Cancelled,
                MeetingResponseType.NoResponseReceived => CalendarItemStatus.NotResponded,
                _ => CalendarItemStatus.Accepted
            },
            Attendees = BuildAttendees(appointment),
            ReminderMinutesBeforeStart = appointment.IsReminderSet ? appointment.ReminderMinutesBeforeStart : null
        };
    }

    private static List<SyncedCalendarAttendee> BuildAttendees(Appointment appointment)
    {
        var attendees = new List<SyncedCalendarAttendee>();
        AddAttendees(attendees, SafeGet(() => appointment.RequiredAttendees), isOptional: false);
        AddAttendees(attendees, SafeGet(() => appointment.OptionalAttendees), isOptional: true);
        return attendees.Count == 0 ? null : attendees;
    }

    private static void AddAttendees(List<SyncedCalendarAttendee> target, AttendeeCollection source, bool isOptional)
    {
        if (source == null)
            return;

        foreach (var attendee in source)
        {
            if (string.IsNullOrEmpty(attendee.Address))
                continue;

            target.Add(new SyncedCalendarAttendee
            {
                Name = attendee.Name,
                Email = attendee.Address,
                IsOptional = isOptional,
                Status = attendee.ResponseType switch
                {
                    MeetingResponseType.Accept => AttendeeStatus.Accepted,
                    MeetingResponseType.Tentative => AttendeeStatus.Tentative,
                    MeetingResponseType.Decline => AttendeeStatus.Declined,
                    _ => AttendeeStatus.NeedsAction
                }
            });
        }
    }

    #endregion

    #region Calendar Operations

    public override List<IRequestBundle<EwsRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var item = request.PreparedItem;
        var attendees = request.ComposeResult?.Attendees;
        var reminders = request.ComposeResult?.SelectedReminders;
        var calendarRemoteId = request.AssignedCalendar.RemoteCalendarId;
        var isRecurring = request.IsRecurring;
        var recurrenceRule = EwsRecurrenceMapper.GetRecurrenceRule(item) ?? request.ComposeResult?.Recurrence;
        var title = item.Title;

        return Bundle(async service =>
        {
            var appointment = new Appointment(service);
            ApplyCalendarItem(appointment, item, attendees, reminders);

            // Stamp the local preview id so the synced event reconciles with the optimistic UI item.
            appointment.SetExtendedProperty(ExchangeCalendarSchema.WinoClientTrackingId, item.Id.ToString("N"));

            // A repeating event is saved as a series master carrying the rule; the server expands it.
            if (isRecurring)
            {
                var recurrence = EwsRecurrenceMapper.Create(recurrenceRule, item.StartDate);

                if (recurrence is null)
                    _logger.Warning("The recurrence rule of '{Title}' has no Exchange equivalent; created it as a single event.", title);
                else
                    appointment.Recurrence = recurrence;
            }

            var sendMode = HasAttendees(appointment) ? SendInvitationsMode.SendToAllAndSaveCopy : SendInvitationsMode.SendToNone;
            await appointment.Save(new FolderId(calendarRemoteId), sendMode).ConfigureAwait(false);

            // The calendar view returns a series as its occurrences, so a master row stored here would
            // never be confirmed by a sync; the resync after this request stores the occurrences.
            if (!isRecurring)
            {
                await _exchangeChangeProcessor.PersistCreatedCalendarEventAsync(
                    item,
                    request.PreparedEvent.Attendees,
                    request.PreparedEvent.Reminders,
                    appointment.Id.UniqueId.WithClientTrackingId(item.Id)).ConfigureAwait(false);
            }
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var item = request.Item;
        var attendees = request.Attendees;

        return Bundle(async service =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(item.RemoteEventId.GetProviderRemoteEventId())).ConfigureAwait(false);
            ApplyCalendarItem(appointment, item, attendees, reminders: null);

            var sendMode = HasAttendees(appointment)
                ? SendInvitationsOrCancellationsMode.SendToAllAndSaveCopy
                : SendInvitationsOrCancellationsMode.SendToNone;
            await appointment.Update(ConflictResolutionMode.AutoResolve, sendMode).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        => UpdateCalendarEvent(request);

    public override List<IRequestBundle<EwsRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
    {
        var remoteId = request.Item.RemoteEventId;

        return Bundle(async service =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(remoteId.GetProviderRemoteEventId()), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
            await appointment.Delete(DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> AcceptEvent(AcceptEventRequest request)
        => RespondToEvent(request, RsvpResponse.Accept, request.Item.RemoteEventId, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> TentativeEvent(TentativeEventRequest request)
        => RespondToEvent(request, RsvpResponse.Tentative, request.Item.RemoteEventId, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> DeclineEvent(DeclineEventRequest request)
        => RespondToEvent(request, RsvpResponse.Decline, request.Item.RemoteEventId, request.ResponseMessage);

    private enum RsvpResponse { Accept, Tentative, Decline }

    private List<IRequestBundle<EwsRequest>> RespondToEvent(CalendarRequestBase request, RsvpResponse response, string remoteEventId, string responseMessage)
    {
        return Bundle(async service =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(remoteEventId.GetProviderRemoteEventId()), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);

            switch (response)
            {
                case RsvpResponse.Accept:
                {
                    var message = appointment.CreateAcceptMessage(false);
                    if (!string.IsNullOrEmpty(responseMessage)) message.Body = new MessageBody(responseMessage);
                    await message.SendAndSaveCopy().ConfigureAwait(false);
                    break;
                }
                case RsvpResponse.Tentative:
                {
                    var message = appointment.CreateAcceptMessage(true);
                    if (!string.IsNullOrEmpty(responseMessage)) message.Body = new MessageBody(responseMessage);
                    await message.SendAndSaveCopy().ConfigureAwait(false);
                    break;
                }
                case RsvpResponse.Decline:
                {
                    var message = appointment.CreateDeclineMessage();
                    if (!string.IsNullOrEmpty(responseMessage)) message.Body = new MessageBody(responseMessage);
                    await message.SendAndSaveCopy().ConfigureAwait(false);
                    break;
                }
            }
        }, request, request);
    }

    private static bool HasAttendees(Appointment appointment)
        => appointment.RequiredAttendees.Count > 0 || appointment.OptionalAttendees.Count > 0;

    private static void ApplyCalendarItem(Appointment appointment, CalendarItem item, List<CalendarEventAttendee> attendees, List<Reminder> reminders)
    {
        appointment.Subject = item.Title;
        appointment.Body = new MessageBody(BodyType.HTML, item.Description ?? string.Empty);
        appointment.Location = item.Location;

        // item.StartDate is wall-clock in item.StartTimeZone; set the appointment's time zone so EWS
        // interprets the wall-clock time correctly regardless of the service time zone.
        var timeZone = ResolveTimeZoneInfo(item.StartTimeZone);
        appointment.StartTimeZone = timeZone;
        appointment.EndTimeZone = timeZone;
        appointment.Start = item.StartDate;
        appointment.End = item.StartDate.AddSeconds(item.DurationInSeconds);
        appointment.IsAllDayEvent = item.IsAllDayEvent;

        appointment.LegacyFreeBusyStatus = item.ShowAs switch
        {
            CalendarItemShowAs.Free => LegacyFreeBusyStatus.Free,
            CalendarItemShowAs.Tentative => LegacyFreeBusyStatus.Tentative,
            CalendarItemShowAs.OutOfOffice => LegacyFreeBusyStatus.OOF,
            CalendarItemShowAs.WorkingElsewhere => LegacyFreeBusyStatus.WorkingElsewhere,
            _ => LegacyFreeBusyStatus.Busy
        };

        if (attendees != null)
        {
            appointment.RequiredAttendees.Clear();
            appointment.OptionalAttendees.Clear();

            foreach (var attendee in attendees)
            {
                if (string.IsNullOrEmpty(attendee.Email))
                    continue;

                if (attendee.IsOptionalAttendee)
                    appointment.OptionalAttendees.Add(attendee.Email);
                else
                    appointment.RequiredAttendees.Add(attendee.Email);
            }
        }

        if (reminders != null && reminders.Count > 0)
        {
            appointment.IsReminderSet = true;
            appointment.ReminderMinutesBeforeStart = (int)Math.Max(0, reminders[0].DurationInSeconds / 60);
        }
    }

    private static TimeZoneInfo ResolveTimeZoneInfo(string ianaTimeZone)
    {
        if (string.IsNullOrEmpty(ianaTimeZone))
            return TimeZoneInfo.Utc;

        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZone, out var windowsId))
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
        catch
        {
            // Fall through to the direct lookup / UTC.
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    #endregion

    #region Contacts

    // The mailbox's default Contacts folder is the account's one provider address book. Exchange has
    // no address-book management from this client, and a contact photo is not written back.

    protected override Task<ContactSynchronizationResult> SynchronizeContactsInternalAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default)
        => Account.ContactIntegrationSource switch
        {
            AccountIntegrationSource.Local => _localContactSynchronizer.SynchronizeAsync(options, cancellationToken),
            AccountIntegrationSource.Provider when Account.IsContactAccessGranted && _contactService is not null => SynchronizeProviderContactsAsync(options, cancellationToken),
            _ => Task.FromResult(ContactSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable)))
        };

    protected override Task ExecuteContactRequestsInternalAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
        => Account.ContactIntegrationSource switch
        {
            AccountIntegrationSource.Local => _localContactSynchronizer.ExecuteRequestsAsync(requests, cancellationToken),
            AccountIntegrationSource.Provider when Account.IsContactAccessGranted && _contactService is not null => ExecuteProviderContactRequestsAsync(requests, cancellationToken),
            _ => throw new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable)
        };

    /// <summary>
    /// The account's Exchange address book, created on first use. The account has one, the mailbox's
    /// Contacts folder, so a book stored under another id is the same folder as the other transport
    /// addressed it: it is re-pointed in place rather than dropped and imported again.
    /// </summary>
    protected async Task<ContactAddressBook> GetOrCreateContactsBookAsync(string remoteFolderId)
    {
        var books = (await _contactService.GetAddressBooksAsync(Account.Id).ConfigureAwait(false))
            .Where(b => b.SourceKind == ContactSourceKind.Exchange)
            .ToList();
        var current = books.FirstOrDefault(b => string.Equals(b.RemoteId, remoteFolderId, StringComparison.Ordinal));

        if (current is null && books.OrderByDescending(b => b.IsDefault).FirstOrDefault() is { } carried)
        {
            _logger.Information("Exchange contacts {Account}: re-keying the address book from {Old} to {New}.", Account.Name, carried.RemoteId, remoteFolderId);
            await _contactService.RekeyAddressBookAsync(carried.Id, remoteFolderId, new Dictionary<Guid, string>()).ConfigureAwait(false);
            current = carried;
        }

        foreach (var stale in books.Where(b => b.Id != current?.Id))
            await _contactService.DeleteAddressBookAsync(stale.Id).ConfigureAwait(false);

        return await _contactService.GetOrCreateProviderAddressBookAsync(Account.Id, ContactSourceKind.Exchange, remoteFolderId, Account.Name, true).ConfigureAwait(false);
    }

    /// <summary>
    /// Before a snapshot replaces the book's rows: contacts the other transport stored are matched to
    /// the incoming ones and take their remote ids, so the rebuild (which keeps rows by remote id)
    /// preserves their favourites, list memberships and local identity across a transport change.
    /// </summary>
    protected async Task RekeyContactsFromOtherTransportAsync(ContactAddressBook book, IReadOnlyList<AccountContact> incoming)
    {
        var stored = await _contactService.GetContactsByAddressBookAsync(book.Id).ConfigureAwait(false);
        var matches = ExchangeTransportRekey.MatchContacts(stored, incoming);

        if (matches.Count == 0)
            return;

        _logger.Information("Exchange contacts {Account}: re-keying {Count} contacts stored by the other transport.", Account.Name, matches.Count);
        await _contactService.RekeyAddressBookAsync(book.Id, book.RemoteId, matches).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls a contact's photo when the server has one and the local row does not yet, or the photo
    /// changed since. The fetch is best-effort: a failure never blocks the contact.
    /// </summary>
    protected async Task DownloadContactPhotosAsync(IReadOnlyList<(AccountContact Contact, Func<Task<byte[]>> Fetch)> photos, ContactAddressBook book, CancellationToken cancellationToken)
    {
        if (photos.Count == 0 || _contactPictureFileService is null)
            return;

        var existing = (await _contactService.GetContactsByAddressBookAsync(book.Id).ConfigureAwait(false))
            .Where(c => !string.IsNullOrEmpty(c.RemoteId))
            .GroupBy(c => c.RemoteId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (contact, fetch) in photos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existing.TryGetValue(contact.RemoteId, out var current) && current.ContactPictureFileId is not null &&
                string.Equals(current.RemotePhotoKey, contact.RemotePhotoKey, StringComparison.Ordinal))
                continue;

            try
            {
                var bytes = await fetch().ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                    contact.ContactPictureFileId = await _contactPictureFileService.SaveContactPictureAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Debug(ex, "Exchange contact photo of {RemoteId} not downloaded.", contact.RemoteId);
            }
        }
    }

    // FirstClassProperties loads the full contact (name, e-mails, phones, addresses, company, notes) in one
    // batch without enumerating individual ContactSchema definitions; contacts are few so the cost is trivial.
    private static readonly PropertySet ContactPropertySet = new(BasePropertySet.FirstClassProperties, ItemSchema.Attachments)
    {
        RequestedBodyType = BodyType.Text
    };

    private const int ContactDownloadPageSize = 200;

    /// <summary>
    /// One-way pull of the account's default Exchange Contacts folder into the address book. Mirrors the
    /// calendar pattern: page items, batch-load properties, then replace the book's rows (favorites and
    /// already-downloaded pictures survive the rebuild by remote id).
    /// </summary>
    protected virtual async Task<ContactSynchronizationResult> SynchronizeProviderContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var contactsFolder = await ContactsFolder
            .Bind(service, WellKnownFolderName.Contacts, new PropertySet(BasePropertySet.IdOnly))
            .ConfigureAwait(false);

        _logger.Information("Exchange contacts sync starting for {Account}.", Account.Name);

        var book = await GetOrCreateContactsBookAsync(contactsFolder.Id.UniqueId).ConfigureAwait(false);
        var upserts = new List<AccountContact>();
        var photos = new List<(AccountContact, Func<Task<byte[]>>)>();

        var view = new ItemView(ContactDownloadPageSize);
        FindItemsResults<Item> results;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            results = await service.FindItems(contactsFolder.Id, view).ConfigureAwait(false);

            var contacts = results.Items.OfType<Contact>().ToList();
            if (contacts.Count > 0)
            {
                await service.LoadPropertiesForItems(contacts, ContactPropertySet).ConfigureAwait(false);

                foreach (var contact in contacts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mapped = MapToAccountContact(contact, book);
                    upserts.Add(mapped);

                    if (BuildContactPhotoFetcher(contact) is { } fetch)
                        photos.Add((mapped, fetch));
                }
            }

            view.Offset += results.Items.Count;
        }
        while (results.MoreAvailable);

        await RekeyContactsFromOtherTransportAsync(book, upserts).ConfigureAwait(false);
        await DownloadContactPhotosAsync(photos, book, cancellationToken).ConfigureAwait(false);
        await _contactService.ReplaceAddressBookAsync(book.Id, upserts, null).ConfigureAwait(false);

        _logger.Information("Exchange contacts sync for {Account}: {Count} server contacts.", Account.Name, upserts.Count);

        return ContactSynchronizationResult.Completed(upserts.Count, upserts.Count, 0);
    }

    protected virtual async Task ExecuteProviderContactRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = await ResolveRequestedContactAsync(request).ConfigureAwait(false);

            switch (request.Operation)
            {
                case ContactSynchronizerOperation.Create:
                {
                    var contact = new Contact(service);
                    ApplyContactProperties(contact, local);
                    await contact.Save(WellKnownFolderName.Contacts).ConfigureAwait(false);

                    var mapped = RequestEntityCloner.Contact(local);
                    mapped.RemoteId = contact.Id.UniqueId;
                    mapped.RemoteVersion = contact.Id.ChangeKey;
                    mapped.SourceKind = ContactSourceKind.Exchange;
                    await _exchangeChangeProcessor.CommitContactMutationAsync(local.Id, mapped, false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Update:
                {
                    var contact = await Contact.Bind(service, new ItemId(local.RemoteId)).ConfigureAwait(false);
                    ApplyContactProperties(contact, local);
                    await contact.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);

                    var mapped = RequestEntityCloner.Contact(local);
                    mapped.RemoteVersion = contact.Id.ChangeKey;
                    await _exchangeChangeProcessor.CommitContactMutationAsync(local.Id, mapped, false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Delete:
                {
                    if (!string.IsNullOrWhiteSpace(local?.RemoteId))
                    {
                        try
                        {
                            var contact = await Contact.Bind(service, new ItemId(local.RemoteId), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                            await contact.Delete(DeleteMode.MoveToDeletedItems).ConfigureAwait(false);
                        }
                        catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorItemNotFound)
                        {
                            _logger.Warning("Exchange contact {RemoteId} already absent on delete; treating as done.", local.RemoteId);
                        }
                    }

                    if (local is not null)
                        await _exchangeChangeProcessor.CommitContactMutationAsync(local.Id, null, true).ConfigureAwait(false);
                    break;
                }
                default:
                    throw UnsupportedContactOperation(request.Operation);
            }
        }
    }

    /// <summary>The contact a request acts on: the request's snapshot, or the stored row when the snapshot lacks the server id.</summary>
    protected async Task<AccountContact> ResolveRequestedContactAsync(IContactActionRequest request)
    {
        var typedRequest = request as ContactActionRequest;
        var local = typedRequest?.Contact;

        if (request.Operation != ContactSynchronizerOperation.Create && string.IsNullOrWhiteSpace(local?.RemoteId))
            local = await _contactService.GetContactAsync(request.LocalContactId).ConfigureAwait(false)
                    ?? typedRequest?.OriginalContact
                    ?? local;

        if (local is null && request.Operation != ContactSynchronizerOperation.Delete)
            throw new InvalidOperationException($"Contact {request.LocalContactId} is unavailable for {request.Operation}.");

        if (request.Operation is ContactSynchronizerOperation.Update && string.IsNullOrWhiteSpace(local?.RemoteId))
            throw new InvalidOperationException($"Contact {request.LocalContactId} has no server identity for {request.Operation}.");

        return local;
    }

    /// <summary>Photos and address books are not written back to Exchange from this client.</summary>
    protected static NotSupportedException UnsupportedContactOperation(ContactSynchronizerOperation operation)
        => operation is ContactSynchronizerOperation.SetPhoto or ContactSynchronizerOperation.DeletePhoto
            ? new NotSupportedException(Translator.Synchronizer_ExchangeContactPhotoUnsupported)
            : new NotSupportedException(Translator.Synchronizer_ExchangeAddressBooksUnsupported);

    // Maps the local contact's editable fields onto a bound EWS Contact (inverse of MapToAccountContact).
    // First-class fields (DisplayName/Company/JobTitle/Notes) may be cleared by setting null; indexed
    // properties (e-mail, phones) are only set when present to avoid EWS dictionary clear quirks. The
    // postal addresses are intentionally not pushed.
    private static void ApplyContactProperties(Contact contact, AccountContact item)
    {
        contact.DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.PrimaryEmailAddress : item.DisplayName;
        contact.GivenName = item.GivenName;
        contact.Surname = item.Surname;
        contact.CompanyName = item.CompanyName;
        contact.JobTitle = item.JobTitle;

        var keys = new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 };
        var addresses = (item.EmailAddresses ?? []).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Order).Select(a => a.Address).Where(a => !string.IsNullOrWhiteSpace(a)).Take(3).ToList();
        for (var i = 0; i < addresses.Count; i++)
            contact.EmailAddresses[keys[i]] = new EmailAddress(addresses[i]);

        SetPhone(contact, PhoneNumberKey.BusinessPhone, FirstPhone(item, ContactPhoneKind.Work));
        SetPhone(contact, PhoneNumberKey.HomePhone, FirstPhone(item, ContactPhoneKind.Home));
        SetPhone(contact, PhoneNumberKey.MobilePhone, FirstPhone(item, ContactPhoneKind.Mobile));

        if (!string.IsNullOrWhiteSpace(item.Notes))
            contact.Body = new MessageBody(BodyType.Text, item.Notes);
    }

    private static string FirstPhone(AccountContact item, ContactPhoneKind kind)
        => item.PhoneNumbers?.Where(p => p.Kind == kind).OrderBy(p => p.Order).Select(p => p.Number).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

    private static void SetPhone(Contact contact, PhoneNumberKey key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            contact.PhoneNumbers[key] = value;
    }

    private AccountContact MapToAccountContact(Contact remote, ContactAddressBook book)
    {
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = Account.Id,
            AddressBookId = book.Id,
            SourceKind = ContactSourceKind.Exchange,
            RemoteId = remote.Id.UniqueId,
            RemoteVersion = remote.Id.ChangeKey,
            RemotePhotoKey = HasContactPhoto(remote) ? remote.Id.ChangeKey : null,
            DisplayName = SafeGet(() => remote.DisplayName),
            GivenName = SafeGet(() => remote.GivenName),
            MiddleName = SafeGet(() => remote.MiddleName),
            Surname = SafeGet(() => remote.Surname),
            Nickname = SafeGet(() => remote.NickName),
            FileAs = SafeGet(() => remote.FileAs),
            CompanyName = SafeGet(() => remote.CompanyName),
            Department = SafeGet(() => remote.Department),
            JobTitle = SafeGet(() => remote.JobTitle),
            OfficeLocation = SafeGet(() => remote.OfficeLocation),
            Profession = SafeGet(() => remote.Profession),
            Notes = SafeGet(() => remote.Body?.Text),
            Website = SafeGet(() => remote.BusinessHomePage),
            PendingMutation = ContactPendingMutation.None
        };

        var birthday = SafeGet(() => (DateTime?)remote.Birthday);
        if (birthday is { } b)
        {
            contact.BirthdayYear = b.Year;
            contact.BirthdayMonth = b.Month;
            contact.BirthdayDay = b.Day;
        }

        var order = 0;
        foreach (var key in new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 })
        {
            var address = GetEmail(remote, key);
            if (string.IsNullOrWhiteSpace(address))
                continue;

            contact.EmailAddresses.Add(new ContactEmailAddress
            {
                Id = Guid.NewGuid(),
                ContactId = contact.Id,
                Address = address,
                NormalizedAddress = ContactEmailAddress.Normalize(address),
                Order = order,
                IsPrimary = order == 0
            });
            order++;
        }

        AddPhone(contact, GetPhone(remote, PhoneNumberKey.BusinessPhone), ContactPhoneKind.Work);
        AddPhone(contact, GetPhone(remote, PhoneNumberKey.HomePhone), ContactPhoneKind.Home);
        AddPhone(contact, GetPhone(remote, PhoneNumberKey.MobilePhone), ContactPhoneKind.Mobile);

        AddAddress(contact, GetAddress(remote, PhysicalAddressKey.Business), ContactPostalAddressKind.Business);
        AddAddress(contact, GetAddress(remote, PhysicalAddressKey.Home), ContactPostalAddressKind.Home);
        AddAddress(contact, GetAddress(remote, PhysicalAddressKey.Other), ContactPostalAddressKind.Other);

        return contact;
    }

    private static void AddPhone(AccountContact contact, string number, ContactPhoneKind kind)
    {
        if (string.IsNullOrWhiteSpace(number))
            return;

        contact.PhoneNumbers.Add(new ContactPhoneNumber
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            Number = number.Trim(),
            Kind = kind,
            Order = contact.PhoneNumbers.Count,
            IsPrimary = contact.PhoneNumbers.Count == 0
        });
    }

    private static void AddAddress(AccountContact contact, PhysicalAddressEntry address, ContactPostalAddressKind kind)
    {
        if (address is null)
            return;

        var parts = new[] { address.Street, address.City, address.State, address.PostalCode, address.CountryOrRegion };
        if (parts.All(string.IsNullOrWhiteSpace))
            return;

        contact.PostalAddresses.Add(new ContactPostalAddress
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            Kind = kind,
            Street = address.Street,
            City = address.City,
            Region = address.State,
            PostalCode = address.PostalCode,
            Country = address.CountryOrRegion
        });
    }

    // Every indexed read goes through a guard: a key that was never set throws on some servers instead
    // of reporting absence, and one such contact would otherwise fail the whole book's sync.
    private static string GetEmail(Contact contact, EmailAddressKey key)
        => SafeGet(() => contact.EmailAddresses != null && contact.EmailAddresses.TryGetValue(key, out var email) ? email?.Address?.Trim() : null);

    private static string GetPhone(Contact contact, PhoneNumberKey key)
        => SafeGet(() => contact.PhoneNumbers != null && contact.PhoneNumbers.TryGetValue(key, out var number) ? number : null);

    private static PhysicalAddressEntry GetAddress(Contact contact, PhysicalAddressKey key)
        => SafeGet(() => contact.PhysicalAddresses != null && contact.PhysicalAddresses.TryGetValue(key, out var address) ? address : null);

    private static bool HasContactPhoto(Contact contact)
        => SafeGet(() => contact.Attachments?.OfType<FileAttachment>().Any(a => a.IsContactPhoto)) ?? false;

    // Returns a lazy fetcher for a contact's photo (the IsContactPhoto file attachment), or null when the
    // contact has none. It is invoked only when the photo actually needs storing, so contacts that already
    // have a local picture cost no extra EWS round-trip.
    private static Func<Task<byte[]>> BuildContactPhotoFetcher(Contact contact)
    {
        var photoAttachment = SafeGet(() => contact.Attachments?.OfType<FileAttachment>().FirstOrDefault(a => a.IsContactPhoto));
        if (photoAttachment == null)
            return null;

        return () =>
        {
            // EWS FileAttachment exposes only a synchronous Load() in this NETCore port (no LoadAsync); offload the
            // blocking call to the thread pool so the awaiting caller's thread isn't held for the HTTP fetch.
            return Task.Run(() =>
            {
                photoAttachment.Load();
                return photoAttachment.Content;
            });
        };
    }

    #endregion

    #region Tasks

    // The mailbox's default Tasks folder is the account's one provider task list. Exchange tasks have
    // no checklist, and task folders are not managed from this client.

    protected override Task<TaskSynchronizationResult> SynchronizeTasksInternalAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.TaskIntegrationSource == AccountIntegrationSource.Local)
            return _localTaskSynchronizer.SynchronizeAsync(options, cancellationToken);

        if (Account.TaskIntegrationSource != AccountIntegrationSource.Provider || !Account.IsTaskAccessGranted || _taskService is null)
            return Task.FromResult(TaskSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_TasksUnavailable)));

        return SynchronizeProviderTasksAsync(options, cancellationToken);
    }

    protected override Task ExecuteTaskRequestsInternalAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default)
    {
        if (Account.TaskIntegrationSource == AccountIntegrationSource.Local || requests.All(IsLocalTaskRequest))
            return _localTaskSynchronizer.ExecuteRequestsAsync(requests, cancellationToken);

        if (Account.TaskIntegrationSource != AccountIntegrationSource.Provider || !Account.IsTaskAccessGranted || _taskService is null)
            throw new InvalidOperationException(Translator.Synchronizer_TasksUnavailable);

        return ExecuteProviderTaskRequestsAsync(requests, cancellationToken);
    }

    private static bool IsLocalTaskRequest(ITaskActionRequest request)
        => request is TaskActionRequest taskRequest &&
           (taskRequest.List?.SourceKind ??
            taskRequest.Task?.SourceKind ??
            taskRequest.Step?.SourceKind ??
            taskRequest.Group?.SourceKind) == TaskSourceKind.Local;

    protected override bool ShouldReconcileTaskRequests(IReadOnlyList<ITaskActionRequest> requests)
        => !requests.All(IsLocalTaskRequest);

    /// <summary>The account's Exchange task list for the given folder, replacing any list keyed by an older folder id.</summary>
    protected async Task<AccountTaskList> EnsureTaskListAsync(string remoteFolderId, string title)
    {
        // The account has one Exchange list, the mailbox's Tasks folder. A list stored under another
        // id is that folder as the other transport addressed it: re-point it so the reconciliation
        // below keeps the row (colour, group, placement, "last used" preference) instead of replacing it.
        var exchangeLists = (await _taskService.GetTaskListsAsync(Account.Id).ConfigureAwait(false))
            .Where(list => list.SourceKind == TaskSourceKind.Exchange)
            .ToList();

        if (!exchangeLists.Any(list => string.Equals(list.RemoteId, remoteFolderId, StringComparison.Ordinal)) &&
            exchangeLists.OrderByDescending(list => list.IsDefault).FirstOrDefault() is { } carried)
        {
            _logger.Information("Exchange tasks {Account}: re-keying the task list from {Old} to {New}.", Account.Name, carried.RemoteId, remoteFolderId);
            await _taskService.RekeyTaskListAsync(carried.Id, remoteFolderId, new Dictionary<Guid, string>()).ConfigureAwait(false);
        }

        await _taskService.ApplyTaskTopologyDeltaAsync(new TaskTopologyDelta
        {
            MailAccountId = Account.Id,
            SourceKind = TaskSourceKind.Exchange,
            Lists =
            [
                new AccountTaskList
                {
                    MailAccountId = Account.Id,
                    SourceKind = TaskSourceKind.Exchange,
                    RemoteId = remoteFolderId,
                    Title = string.IsNullOrWhiteSpace(title) ? "Tasks" : title,
                    IsDefault = true,
                    IsReadOnly = false
                }
            ],
            ReconcileLists = true
        }).ConfigureAwait(false);

        return (await _taskService.GetTaskListsAsync(Account.Id).ConfigureAwait(false))
            .FirstOrDefault(list => list.SourceKind == TaskSourceKind.Exchange && string.Equals(list.RemoteId, remoteFolderId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The Exchange task list could not be stored.");
    }

    /// <summary>
    /// Replaces the list's rows with the server's, keeping the stored identity of rows already known by
    /// remote id so a reconciliation racing a create does not double the task.
    /// </summary>
    protected async Task<TaskSynchronizationResult> ApplyTaskSnapshotAsync(AccountTaskList list, List<AccountTask> tasks)
    {
        var current = await _taskService.GetTasksAsync(listId: list.Id).ConfigureAwait(false);

        // Tasks the other transport stored take the incoming ids, so the snapshot below keeps their
        // rows (and with them the My Day date) instead of importing them again after a transport change.
        var rekeyed = ExchangeTransportRekey.MatchTasks(current, tasks);
        if (rekeyed.Count > 0)
        {
            _logger.Information("Exchange tasks {Account}: re-keying {Count} tasks stored by the other transport.", Account.Name, rekeyed.Count);
            await _taskService.RekeyTaskListAsync(list.Id, list.RemoteId, rekeyed).ConfigureAwait(false);

            foreach (var task in current)
            {
                if (rekeyed.TryGetValue(task.Id, out var remoteId))
                    task.RemoteId = remoteId;
            }
        }

        var currentByRemoteId = current.Where(task => !string.IsNullOrWhiteSpace(task.RemoteId))
            .GroupBy(task => task.RemoteId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (task.RemoteId is not null && currentByRemoteId.TryGetValue(task.RemoteId, out var existing))
                task.Id = existing.Id;
        }

        var deleted = current.Count(task => task.PendingMutation == TaskPendingMutation.None &&
                                            task.RemoteId is not null &&
                                            !tasks.Any(t => string.Equals(t.RemoteId, task.RemoteId, StringComparison.Ordinal)));

        await _taskService.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta
        {
            TaskListId = list.Id,
            Tasks = tasks,
            AuthoritativeStepParentRemoteIds = tasks.Select(task => task.RemoteId).Where(id => id is not null).ToList(),
            IsFullSnapshot = true,
            WatermarkUtc = DateTime.UtcNow
        }).ConfigureAwait(false);

        return TaskSynchronizationResult.Completed(tasks.Count, tasks.Count, deleted);
    }

    /// <summary>The task a request acts on, with the stored server identity applied over the request's snapshot.</summary>
    protected async Task<(AccountTask Task, AccountTaskList List)> ResolveRequestedTaskAsync(ITaskActionRequest request)
    {
        var typedRequest = request as TaskActionRequest;
        var requestedTask = typedRequest?.Task ?? typedRequest?.OriginalTask;
        var storedTask = await _taskService.GetTaskAsync(request.TaskId ?? Guid.Empty).ConfigureAwait(false);
        var localTask = RequestEntityCloner.Task(requestedTask ?? storedTask)
            ?? throw new InvalidOperationException($"Task {request.TaskId} is unavailable for {request.Operation}.");

        if (storedTask is not null)
        {
            localTask.RemoteId = storedTask.RemoteId ?? localTask.RemoteId;
            localTask.RemoteVersion = storedTask.RemoteVersion ?? localTask.RemoteVersion;
        }

        var list = await _taskService.GetTaskListAsync(localTask.TaskListId).ConfigureAwait(false);
        if (list?.RemoteId is null)
            throw new InvalidOperationException($"Task list {localTask.TaskListId} is unavailable for {request.Operation}.");

        return (localTask, list);
    }

    /// <summary>Commits a task write: the local snapshot under its server identity, as the Gmail and Outlook paths do.</summary>
    protected async Task CommitTaskAsync(ITaskActionRequest request, AccountTask localTask, string remoteId, string remoteVersion)
    {
        var mapped = RequestEntityCloner.Task(localTask);
        mapped.RemoteId = remoteId;
        mapped.RemoteVersion = remoteVersion;
        mapped.SourceKind = TaskSourceKind.Exchange;
        mapped.PendingMutation = TaskPendingMutation.None;
        mapped.Steps = [];

        await _exchangeChangeProcessor.CommitTaskMutationAsync(localTask.Id, mapped, false, localTask, request.Operation).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists, groups and steps have no Exchange counterpart this client writes. Group and placement
    /// edits are local bookkeeping and complete locally, as the Gmail path does; the rest is refused.
    /// </summary>
    protected async Task<bool> TryCompleteLocalTaskRequestAsync(ITaskActionRequest request)
    {
        var typedRequest = request as TaskActionRequest;
        switch (request.Operation)
        {
            case TaskSynchronizerOperation.CreateGroup:
            case TaskSynchronizerOperation.UpdateGroup:
                await _taskService.CompleteTaskListGroupMutationAsync(typedRequest.Group.Id, typedRequest.Group, false).ConfigureAwait(false);
                return true;
            case TaskSynchronizerOperation.DeleteGroup:
                await _taskService.CompleteTaskListGroupMutationAsync(typedRequest.Group.Id, null, true).ConfigureAwait(false);
                return true;
            case TaskSynchronizerOperation.UpdateListPlacement:
                await _taskService.CompleteTaskListPlacementMutationAsync(typedRequest.List.Id, typedRequest.List).ConfigureAwait(false);
                return true;
            case TaskSynchronizerOperation.CreateTask:
            case TaskSynchronizerOperation.UpdateTask:
            case TaskSynchronizerOperation.DeleteTask:
                return false;
            default:
                throw new NotSupportedException(Translator.Synchronizer_ExchangeTaskListsUnsupported);
        }
    }

    private static readonly PropertySet TaskPropertySet = new(
        BasePropertySet.IdOnly,
        TaskSchema.Subject,
        TaskSchema.Body,
        TaskSchema.DueDate,
        TaskSchema.Importance,
        TaskSchema.IsComplete,
        TaskSchema.CompleteDate)
    {
        RequestedBodyType = BodyType.Text
    };

    /// <summary>
    /// One-way pull of the account's default Exchange Tasks folder into the task list. Mirrors the
    /// contacts pull: page items, batch-load properties, then replace the list's rows by remote id.
    /// </summary>
    protected virtual async Task<TaskSynchronizationResult> SynchronizeProviderTasksAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var tasksFolder = await TasksFolder
            .Bind(service, WellKnownFolderName.Tasks, new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName))
            .ConfigureAwait(false);

        _logger.Information("Exchange tasks sync starting for {Account}.", Account.Name);

        var list = await EnsureTaskListAsync(tasksFolder.Id.UniqueId, SafeGet(() => tasksFolder.DisplayName)).ConfigureAwait(false);
        var tasks = new List<AccountTask>();

        var view = new ItemView(ContactDownloadPageSize);
        FindItemsResults<Item> results;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            results = await service.FindItems(tasksFolder.Id, view).ConfigureAwait(false);

            var page = results.Items.OfType<EwsTask>().ToList();
            if (page.Count > 0)
            {
                await service.LoadPropertiesForItems(page, TaskPropertySet).ConfigureAwait(false);

                foreach (var task in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tasks.Add(MapToAccountTask(task, list));
                }
            }

            view.Offset += results.Items.Count;
        }
        while (results.MoreAvailable);

        var result = await ApplyTaskSnapshotAsync(list, tasks).ConfigureAwait(false);

        _logger.Information("Exchange tasks sync for {Account}: {Count} server tasks, {Removed} removed.", Account.Name, tasks.Count, result.DeletedCount);

        return result;
    }

    protected virtual async Task ExecuteProviderTaskRequestsAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryCompleteLocalTaskRequestAsync(request).ConfigureAwait(false))
            {
                MarkTaskRequestProcessed(request);
                continue;
            }

            var (localTask, _) = await ResolveRequestedTaskAsync(request).ConfigureAwait(false);

            if (request.Operation == TaskSynchronizerOperation.DeleteTask)
            {
                if (!string.IsNullOrWhiteSpace(localTask.RemoteId))
                {
                    try
                    {
                        var task = await EwsTask.Bind(service, new ItemId(localTask.RemoteId), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                        await task.Delete(DeleteMode.MoveToDeletedItems).ConfigureAwait(false);
                    }
                    catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorItemNotFound)
                    {
                        // Already gone on the server: a delete of a missing item is a no-op success.
                        _logger.Warning("Exchange task {RemoteTaskId} already absent on delete; treating as done.", localTask.RemoteId);
                    }
                }

                await _exchangeChangeProcessor.CommitTaskMutationAsync(localTask.Id, null, true, localTask, request.Operation).ConfigureAwait(false);
                MarkTaskRequestProcessed(request);
                continue;
            }

            EwsTask ewsTask;
            if (string.IsNullOrWhiteSpace(localTask.RemoteId))
            {
                ewsTask = new EwsTask(service);
                ApplyTaskProperties(ewsTask, localTask);
                await ewsTask.Save(WellKnownFolderName.Tasks).ConfigureAwait(false);
            }
            else
            {
                ewsTask = await EwsTask.Bind(service, new ItemId(localTask.RemoteId)).ConfigureAwait(false);
                ApplyTaskProperties(ewsTask, localTask);
                await ewsTask.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            }

            // Tasks have no natural key; the commit stamps the server id onto the local row so the next
            // pull reconciles it in place instead of creating a duplicate.
            await CommitTaskAsync(request, localTask, ewsTask.Id.UniqueId, ewsTask.Id.ChangeKey).ConfigureAwait(false);
            MarkTaskRequestProcessed(request);
        }
    }

    // Maps the local task's editable fields onto a bound EWS Task (inverse of MapToAccountTask).
    private static void ApplyTaskProperties(EwsTask task, AccountTask item)
    {
        task.Subject = item.Title ?? string.Empty;
        task.Body = new MessageBody(BodyType.Text, item.Notes ?? string.Empty);

        // EWS Task start/due are non-nullable; only set them when the local task has a due date.
        if (item.DueDate is DateTime due)
        {
            task.StartDate = due;
            task.DueDate = due;
        }

        task.Importance = item.IsImportant ? Importance.High : Importance.Normal;
        task.Status = item.IsCompleted ? EwsTaskStatus.Completed : EwsTaskStatus.NotStarted;
    }

    private AccountTask MapToAccountTask(EwsTask task, AccountTaskList list)
    {
        // The TryGetProperty type argument must be the nullable DateTime; a bare DateTime throws.
        var completed = SafeGet(() => (bool?)task.IsComplete) ?? false;

        return new AccountTask
        {
            Id = Guid.NewGuid(),
            MailAccountId = Account.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Exchange,
            RemoteId = task.Id.UniqueId,
            RemoteVersion = task.Id.ChangeKey,
            Title = SafeGet(() => task.Subject) ?? string.Empty,
            Notes = SafeGet(() => task.Body?.Text),
            DueDate = task.TryGetProperty(TaskSchema.DueDate, out DateTime? dueDate) ? dueDate?.Date : null,
            IsImportant = SafeGet(() => (Importance?)task.Importance) == Importance.High,
            IsCompleted = completed,
            CompletedAtUtc = completed && task.TryGetProperty(TaskSchema.CompleteDate, out DateTime? completeDate) ? completeDate : null,
            PendingMutation = TaskPendingMutation.None
        };
    }

    #endregion

    /// <summary>
    /// Reconciles the remote mail folder hierarchy into local MailItemFolders:
    /// inserts new folders, updates renamed/moved ones, and deletes folders no longer
    /// present remotely. Special-folder types are detected by binding well-known folders.
    /// </summary>
    protected virtual async Task SynchronizeFoldersAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var (specialMap, unresolvedTypes) = await BuildSpecialFolderMapAsync(service).ConfigureAwait(false);

        var view = new FolderView(500)
        {
            Traversal = FolderTraversal.Deep,
            PropertySet = new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass, FolderSchema.ParentFolderId)
        };

        var remoteFolders = new List<Folder>();
        FindFoldersResults page;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = await service.FindFolders(WellKnownFolderName.MsgFolderRoot, view).ConfigureAwait(false);
            remoteFolders.AddRange(page.Folders.Where(IsMailFolder));
            if (page.NextPageOffset.HasValue)
                view.Offset = page.NextPageOffset.Value;
        }
        while (page.MoreAvailable);

        var localFolders = await _exchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var localByRemoteId = localFolders
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .GroupBy(f => f.RemoteFolderId)
            .ToDictionary(g => g.Key, g => g.First());

        // Track whether the hierarchy changed (folder added/removed/renamed/reparented) so we can
        // refresh the navigation tree, as the Outlook/Gmail/IMAP synchronizers broadcast it.
        var structureChanged = false;

        foreach (var remote in remoteFolders)
        {
            var remoteId = remote.Id.UniqueId;
            // SpecialFolderType's default (0) is Inbox, so a plain TryGetValue would mistag every ordinary
            // user folder as a sticky system Inbox. Fall back to Other when the folder isn't a well-known one.
            var specialType = specialMap.TryGetValue(remoteId, out var mappedType) ? mappedType : SpecialFolderType.Other;
            var isSystem = specialType != SpecialFolderType.Other;

            if (localByRemoteId.TryGetValue(remoteId, out var existing))
            {
                var newParentId = remote.ParentFolderId?.UniqueId;
                if (existing.FolderName != remote.DisplayName || existing.ParentRemoteFolderId != newParentId)
                    structureChanged = true;

                // A well-known bind that failed this sync says nothing about the folder. Demoting a known system
                // folder on that evidence (as an earlier build did) unpinned the Inbox to a plain user folder under
                // "Folders"; the next good sync restored the type but never the pin. Keep what we know instead.
                if (existing.IsSystemFolder && !isSystem && unresolvedTypes.Contains(existing.SpecialFolderType))
                {
                    specialType = existing.SpecialFolderType;
                    isSystem = true;
                }

                // Heal folders an earlier build mistagged as system (defaulted to Inbox, wrongly sticky/system).
                // With unresolved binds excluded above, a system folder that resolves to Other really was mistagged.
                if (existing.IsSystemFolder && !isSystem)
                {
                    existing.IsSticky = false;
                    structureChanged = true;
                }

                // And the reverse: a folder first seen as a plain user folder that now resolves to a well-known
                // one (the well-known bind failed on an earlier sync, see BuildSpecialFolderMapAsync) must be
                // pinned. Without this an Inbox that missed its first bind sat under "Folders" forever.
                if (!existing.IsSystemFolder && isSystem)
                {
                    existing.IsSticky = true;
                    structureChanged = true;
                }

                // The Inbox is never optional: account switch lands on it and the nav only looks for it at the
                // top level, so an unpinned Inbox is a broken account, not a preference. Repairs installs that
                // went through the demote/re-promote cycle above before this build.
                if (specialType == SpecialFolderType.Inbox && !existing.IsSticky)
                {
                    existing.IsSticky = true;
                    structureChanged = true;
                }

                existing.FolderName = remote.DisplayName;
                existing.ParentRemoteFolderId = newParentId;
                existing.SpecialFolderType = specialType;
                existing.IsSystemFolder = isSystem;
                await _exchangeChangeProcessor.UpdateFolderAsync(existing).ConfigureAwait(false);
            }
            else
            {
                structureChanged = true;
                await _exchangeChangeProcessor.InsertFolderAsync(new MailItemFolder
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = Account.Id,
                    RemoteFolderId = remoteId,
                    ParentRemoteFolderId = remote.ParentFolderId?.UniqueId,
                    FolderName = remote.DisplayName,
                    SpecialFolderType = specialType,
                    IsSticky = isSystem,
                    IsSystemFolder = isSystem,
                    IsSynchronizationEnabled = true,
                    ShowUnreadCount = specialType != SpecialFolderType.Deleted,
                    IsCountedInAccountTotal = specialType != SpecialFolderType.Deleted,
                }).ConfigureAwait(false);
            }
        }

        // Remove local folders that no longer exist remotely.
        var remoteIds = remoteFolders.Select(f => f.Id.UniqueId).ToHashSet();
        foreach (var local in localFolders)
        {
            if (!string.IsNullOrEmpty(local.RemoteFolderId) && !remoteIds.Contains(local.RemoteFolderId))
            {
                structureChanged = true;
                await _exchangeChangeProcessor.DeleteFolderAsync(Account.Id, local.RemoteFolderId).ConfigureAwait(false);
            }
        }

        // Refresh the navigation tree so server-side creates/moves/deletes surface without an app restart.
        if (structureChanged)
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));

        await ProbeOnlineArchiveAsync(service).ConfigureAwait(false);
    }

    /// <summary>
    /// Detects whether the mailbox has an online archive (in-place archive) provisioned and persists the
    /// flag on the account. The UI keys the local Archive action off it (<see cref="MailAccount.SuppressLocalArchive"/>)
    /// so users do not divert mail into the local Archive folder behind the server-side archive. Only
    /// writes when the flag actually changes; a transient failure keeps the cached state.
    /// </summary>
    protected async Task ProbeOnlineArchiveAsync(ExchangeService service)
    {
        bool hasArchive;
        try
        {
            await service.FindFolders(WellKnownFolderName.ArchiveMsgFolderRoot, new FolderView(1)).ConfigureAwait(false);
            hasArchive = true;
        }
        catch (ServiceResponseException)
        {
            hasArchive = false;   // no archive mailbox provisioned for this user
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Online archive probe failed for {Account}; keeping cached state.", Account.Name);
            return;
        }

        await PersistOnlineArchiveStateAsync(hasArchive).ConfigureAwait(false);
    }

    /// <summary>Persists a probed online-archive flag when it differs from the cached one.</summary>
    protected async Task PersistOnlineArchiveStateAsync(bool hasArchive)
    {
        if (hasArchive == Account.HasOnlineArchive)
            return;

        Account.HasOnlineArchive = hasArchive;
        await _exchangeChangeProcessor.UpdateAccountAsync(Account).ConfigureAwait(false);
        _logger.Information("Online archive {State} for {Account}.", hasArchive ? "detected" : "no longer available", Account.Name);
    }

    /// <summary>
    /// Maps well-known folder ids to their special type. Types whose bind failed (after a retry) are returned
    /// separately so the caller can leave already-classified folders alone rather than demote them.
    /// </summary>
    private async Task<(Dictionary<string, SpecialFolderType> Map, HashSet<SpecialFolderType> Unresolved)> BuildSpecialFolderMapAsync(ExchangeService service)
    {
        var wellKnown = new (WellKnownFolderName Folder, SpecialFolderType Special)[]
        {
            (WellKnownFolderName.Inbox, SpecialFolderType.Inbox),
            (WellKnownFolderName.SentItems, SpecialFolderType.Sent),
            (WellKnownFolderName.Drafts, SpecialFolderType.Draft),
            (WellKnownFolderName.DeletedItems, SpecialFolderType.Deleted),
            (WellKnownFolderName.JunkEmail, SpecialFolderType.Junk),
        };

        // A failed bind here is not harmless: the folder is then inserted as a plain user folder and only the
        // upward heal in SynchronizeFoldersAsync ever corrects it. Inbox and SentItems are bound first, so a
        // cold-start auth hiccup (on-prem Exchange behind an STS does not offer Bearer until challenged) used to
        // lose exactly those two while the rest resolved. Retry each failure once, and always log.
        var map = new Dictionary<string, SpecialFolderType>();
        var unresolved = new HashSet<SpecialFolderType>();
        foreach (var (folder, special) in wellKnown)
        {
            var resolved = false;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var bound = await Folder.Bind(service, folder, new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                    map[bound.Id.UniqueId] = special;
                    resolved = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Legitimately absent folders (no Junk on some mailboxes) land here too; the retry is cheap.
                    _logger.Warning(ex, "Well-known folder {Folder} bind failed for {Account} (attempt {Attempt}).", folder, Account.Name, attempt);
                }
            }

            if (!resolved)
                unresolved.Add(special);
        }

        return (map, unresolved);
    }

    // The EWS folder class that marks a folder as holding mail items (IPF.Note). Used both to filter the
    // remote hierarchy down to mail folders and to stamp newly-created folders so they round-trip back.
    private const string MailFolderClass = "IPF.Note";

    // Mail folders carry the IPF.Note message class; skip calendar/contact/task/etc.
    private static bool IsMailFolder(Folder folder)
        => !string.IsNullOrEmpty(folder.FolderClass)
           && folder.FolderClass.StartsWith(MailFolderClass, StringComparison.OrdinalIgnoreCase);

    // --- Folder write operations ---
    // The new/renamed/removed folder is reconciled into the local MailItemFolder table by the
    // follow-up FoldersOnly sync that WinoRequestDelegator queues after create/delete, so these
    // just perform the server write (no local-id stamping needed; folders reconcile by RemoteFolderId).

    public override List<IRequestBundle<EwsRequest>> CreateRootFolder(CreateRootFolderRequest request)
    {
        var name = request.NewFolderName;
        if (string.IsNullOrWhiteSpace(name))
            return [];

        return Bundle(async service =>
        {
            // FolderClass MUST be IPF.Note so the new folder is recognized as a mail folder; otherwise
            // SynchronizeFoldersAsync's IsMailFolder filter discards it and it never reaches the local tree.
            var folder = new Folder(service) { DisplayName = name, FolderClass = MailFolderClass };
            await folder.Save(WellKnownFolderName.MsgFolderRoot).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> CreateSubFolder(CreateSubFolderRequest request)
    {
        var name = request.NewFolderName;
        var parentId = request.Folder?.RemoteFolderId;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(parentId))
            return [];

        return Bundle(async service =>
        {
            var folder = new Folder(service) { DisplayName = name, FolderClass = MailFolderClass };
            await folder.Save(new FolderId(parentId)).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> RenameFolder(RenameFolderRequest request)
    {
        var id = request.Folder?.RemoteFolderId;
        if (string.IsNullOrEmpty(id))
            return [];

        return Bundle(async service =>
        {
            try
            {
                var folder = await Folder.Bind(service, new FolderId(id)).ConfigureAwait(false);
                folder.DisplayName = request.NewFolderName;
                await folder.Update().ConfigureAwait(false);
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound)
            {
                _logger.Warning("Skipping Exchange folder rename; folder {RemoteFolderId} no longer exists.", id);
            }
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> DeleteFolder(DeleteFolderRequest request)
    {
        var id = request.Folder?.RemoteFolderId;
        if (string.IsNullOrEmpty(id))
            return [];

        return Bundle(async service =>
        {
            try
            {
                // A folder already inside Deleted Items can't be "moved to Deleted Items" again, so a second
                // delete should permanently remove it (matching OWA/Outlook). Hard-delete in that case.
                var deleteMode = await IsInDeletedItemsAsync(request.Folder).ConfigureAwait(false)
                    ? DeleteMode.HardDelete
                    : DeleteMode.MoveToDeletedItems;

                var folder = await Folder.Bind(service, new FolderId(id), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                await folder.Delete(deleteMode).ConfigureAwait(false);
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound)
            {
                _logger.Warning("Exchange folder {RemoteFolderId} already absent on delete; treating as done.", id);
            }
        }, request, request);
    }

    // True when the folder lives anywhere under the Deleted Items folder, walking the locally-known
    // parent chain (no extra server round-trips).
    private async Task<bool> IsInDeletedItemsAsync(MailItemFolder folder)
    {
        if (folder == null)
            return false;

        var locals = await _exchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var deleted = locals.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Deleted);
        if (deleted == null || string.IsNullOrEmpty(deleted.RemoteFolderId))
            return false;

        var byRemoteId = locals
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .GroupBy(f => f.RemoteFolderId)
            .ToDictionary(g => g.Key, g => g.First());

        var parentId = folder.ParentRemoteFolderId;
        var guard = 0;
        while (!string.IsNullOrEmpty(parentId) && guard++ < 64)
        {
            if (parentId == deleted.RemoteFolderId)
                return true;
            if (!byRemoteId.TryGetValue(parentId, out var parent))
                break;
            parentId = parent.ParentRemoteFolderId;
        }

        return false;
    }

    private async Task<List<MailCopy>> SynchronizeFolderItemsAsync(ExchangeService service, MailItemFolder folder, CancellationToken cancellationToken)
    {
        var downloaded = new List<MailCopy>();
        var syncState = folder.DeltaToken;
        var folderId = new FolderId(folder.RemoteFolderId);
        bool moreAvailable;

        _logger.Debug("Synchronizing items for folder {FolderName} ({Account}).", folder.FolderName, Account.Name);

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changes = await service.SyncFolderItems(folderId, ItemMetadataPropertySet, null,
                (int)InitialMessageDownloadCountPerFolder, SyncFolderItemsScope.NormalItems, syncState).ConfigureAwait(false);

            _logger.Debug("SyncFolderItems returned {Count} change(s) for {FolderName}; more available: {MoreAvailable}.",
                changes.Count, folder.FolderName, changes.MoreChangesAvailable);

            foreach (var change in changes)
            {
                switch (change.ChangeType)
                {
                    case ChangeType.Create:
                    case ChangeType.Update:
                        if (change.Item == null) break;
                        var packages = await CreateNewMailPackagesAsync(change.Item, folder, cancellationToken).ConfigureAwait(false);
                        if (packages?.Count > 0)
                        {
                            foreach (var package in packages)
                            {
                                if (await _exchangeChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false))
                                    downloaded.Add(package.Copy);
                            }
                        }
                        break;
                    case ChangeType.Delete:
                        await _exchangeChangeProcessor.DeleteMailsAsync(Account.Id, [change.ItemId.UniqueId]).ConfigureAwait(false);
                        break;
                    case ChangeType.ReadFlagChange:
                        await _exchangeChangeProcessor.ChangeMailReadStatusAsync(change.ItemId.UniqueId, change.IsRead).ConfigureAwait(false);
                        break;
                }
            }

            syncState = changes.SyncState;
            moreAvailable = changes.MoreChangesAvailable;
        }
        while (moreAvailable);

        folder.DeltaToken = syncState;
        await _exchangeChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
        await _exchangeChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

        _logger.Debug("Folder {FolderName} item sync complete. Downloaded {Count} item(s).", folder.FolderName, downloaded.Count);

        return downloaded;
    }

    /// <summary>
    /// Reads an EWS property that may not have been loaded into the item's property bag, yielding the
    /// default instead of throwing ServiceObjectPropertyException.
    /// </summary>
    private static T SafeGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch (ServiceObjectPropertyException) { return default; }
    }

    private MailCopy MapToMailCopy(Item item, MailItemFolder assignedFolder)
    {
        if (item == null)
            return null;

        var email = item as EmailMessage;

        return new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Id = item.Id.UniqueId,
            FolderId = assignedFolder.Id,
            // Every read below goes through SafeGet: reading a property EWS didn't load throws
            // ServiceObjectPropertyException, and one such item would otherwise fail the whole folder's sync.
            ThreadId = SafeGet(() => item.ConversationId?.UniqueId),
            MessageId = SafeGet(() => email?.InternetMessageId),
            Subject = SafeGet(() => item.Subject),
            FromName = SafeGet(() => email?.From?.Name),
            FromAddress = SafeGet(() => email?.From?.Address),
            CreationDate = SafeGet(() => item.DateTimeReceived.ToUniversalTime()),
            IsRead = SafeGet(() => email?.IsRead) ?? true,
            IsFlagged = SafeGet(() => item.Flag?.FlagStatus) == ItemFlagStatus.Flagged,
            HasAttachments = SafeGet(() => (bool?)item.HasAttachments) ?? false,
            Importance = MapImportance(SafeGet(() => (Importance?)item.Importance) ?? Microsoft.Exchange.WebServices.Data.Importance.Normal),
            // Items synced from the Drafts folder must carry IsDraft so selecting one opens the composer
            // (not the read view) and the compose/send flow treats it as an editable draft.
            IsDraft = assignedFolder?.SpecialFolderType == SpecialFolderType.Draft,
            ItemType = MapItemType(SafeGet(() => item.ItemClass)),
        };
    }

    /// <summary>The mail item type a message class maps to (MS-OXOCAL meeting classes); plain mail otherwise.</summary>
    private static MailItemType MapItemType(string itemClass)
    {
        if (string.IsNullOrEmpty(itemClass))
            return MailItemType.Mail;

        if (itemClass.StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase))
            return MailItemType.CalendarInvitation;

        if (itemClass.StartsWith("IPM.Schedule.Meeting.Canceled", StringComparison.OrdinalIgnoreCase))
            return MailItemType.CalendarCancellation;

        if (itemClass.StartsWith("IPM.Schedule.Meeting.Resp", StringComparison.OrdinalIgnoreCase))
            return MailItemType.CalendarResponse;

        return MailItemType.Mail;
    }

    protected static MailImportance MapImportance(Importance importance) => importance switch
    {
        Importance.High => MailImportance.High,
        Importance.Low => MailImportance.Low,
        _ => MailImportance.Normal,
    };

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem, MailKit.ITransferProgress transferProgress = null, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);

        var ewsItem = await Item.Bind(service, new ItemId(mailItem.Id),
            new PropertySet(ItemSchema.MimeContent)).ConfigureAwait(false);

        using var stream = new MemoryStream(ewsItem.MimeContent.Content);
        var mimeMessage = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);

        await _exchangeChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);

        await TryMapCalendarInvitationAsync(service, mailItem, mimeMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Links a meeting request to the calendar item it created, in the shape the Outlook synchronizer
    /// writes: the appointment is found by iCalendar UID in each synced calendar, persisted locally when
    /// the calendar sync has not reached it yet, and the mapping row the reader's invitation card
    /// resolves is written. Failures are logged; the mail itself is already saved.
    /// </summary>
    private async Task TryMapCalendarInvitationAsync(ExchangeService service, MailCopy mailCopy, MimeMessage mimeMessage, CancellationToken cancellationToken)
    {
        var invitation = InvitationDetails.Parse(mimeMessage.GetCalendarContent());
        if (!invitation.IsRequest || string.IsNullOrWhiteSpace(invitation.Uid))
            return;

        var calendars = await _exchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        if (calendars == null || calendars.Count == 0)
            return;

        foreach (var calendar in calendars)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(calendar.RemoteCalendarId))
                continue;

            try
            {
                var view = new ItemView(1) { PropertySet = new PropertySet(BasePropertySet.IdOnly, AppointmentSchema.AppointmentType) };
                var filter = new SearchFilter.IsEqualTo(AppointmentSchema.ICalUid, invitation.Uid);
                var results = await service.FindItems(new FolderId(calendar.RemoteCalendarId), filter, view).ConfigureAwait(false);

                var match = results.Items.OfType<Appointment>().FirstOrDefault();
                if (match == null)
                    continue;

                // The calendar stores occurrences (CalendarView expands a series server-side), so a series
                // request maps to its first occurrence rather than the master the store never keeps.
                var appointmentId = match.Id;
                if (SafeGet(() => (AppointmentType?)match.AppointmentType) == AppointmentType.RecurringMaster)
                {
                    var master = await Appointment.Bind(service, match.Id, new PropertySet(BasePropertySet.IdOnly, AppointmentSchema.FirstOccurrence)).ConfigureAwait(false);
                    appointmentId = SafeGet(() => master.FirstOccurrence?.ItemId);

                    if (appointmentId == null)
                        continue;
                }

                var appointment = await Appointment.Bind(service, appointmentId, EventPropertySet).ConfigureAwait(false);
                await _exchangeChangeProcessor.ManageCalendarEventAsync(MapToSyncedEvent(appointment), calendar, Account).ConfigureAwait(false);

                var localCalendarItem = await _exchangeChangeProcessor.GetCalendarItemAsync(calendar.Id, appointment.Id.UniqueId).ConfigureAwait(false);
                if (localCalendarItem == null)
                    return;

                await _exchangeChangeProcessor.UpsertMailInvitationCalendarMappingAsync(new MailInvitationCalendarMapping
                {
                    Id = Guid.NewGuid(),
                    AccountId = Account.Id,
                    MailCopyId = mailCopy.Id,
                    InvitationUid = invitation.Uid,
                    CalendarId = calendar.Id,
                    CalendarItemId = localCalendarItem.Id,
                    CalendarRemoteEventId = appointment.Id.UniqueId
                }).ConfigureAwait(false);

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to map Exchange calendar invitation mail {MailCopyId} for calendar {CalendarId}", mailCopy.Id, calendar.Id);
            }
        }
    }

    #region Mail & Folder Operations

    // Wraps an EWS operation into a single request bundle (EWS is stateless HTTP; one
    // service handles the whole batch, so per-action batching collapses to one bundle).
    protected static List<IRequestBundle<EwsRequest>> Bundle(Func<ExchangeService, Task> action, IRequestBase request, IUIChangeRequest uiChangeRequest)
        => [new EwsRequestBundle(new EwsRequest((service, _) => action(service), request), request, uiChangeRequest)];

    public override List<IRequestBundle<EwsRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var isRead = requests[0].IsRead;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(async service =>
        {
            foreach (var id in ids)
            {
                var message = await EmailMessage.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, EmailMessageSchema.IsRead)).ConfigureAwait(false);
                if (message.IsRead == isRead) continue;
                message.IsRead = isRead;
                await message.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeFlag(BatchChangeFlagRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var flagged = requests[0].IsFlagged;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(async service =>
        {
            foreach (var id in ids)
            {
                var item = await Item.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, ItemSchema.Flag)).ConfigureAwait(false);
                item.Flag = new Flag { FlagStatus = flagged ? ItemFlagStatus.Flagged : ItemFlagStatus.NotFlagged };
                await item.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            }

            // EWS doesn't echo flag changes back through SyncFolderItems the way it does read state,
            // so persist the flag locally after the server update succeeds; otherwise it's lost on
            // reload and never reaches the mail-list row.
            foreach (var request in requests)
                await _exchangeChangeProcessor.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Move(BatchMoveRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var destination = new FolderId(requests[0].ToFolder.RemoteFolderId);
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(service => service.MoveItems(ids, destination), requests[0], requests);
    }

    // Junk on Exchange goes through EWS MarkAsJunk (2013+): it adds (isJunk) / removes the sender of each
    // item to/from the mailbox's server-side Blocked Senders list AND moves the item to Junk / Inbox in one
    // call. The optimistic local move is handled by the request flow. (MarkAsJunk only touches Blocked
    // Senders; there's no EWS surface for the Safe Senders list, so "Never block" only un-blocks here.)
    public override List<IRequestBundle<EwsRequest>> ChangeJunkState(BatchChangeJunkStateRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var isJunk = requests[0].IsJunk;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(service => service.MarkAsJunk(ids, isJunk, true, default), requests[0], requests);
    }

    #region Public Folders

    // Read-only browse of the organization's public folder hierarchy. Public folders have no sync state
    // and no notifications, so everything here is fetched live on demand and mapped to transient (never
    // persisted) entities; the mailbox tables, the unified inbox, search and the address book are left
    // untouched. The EWS Managed API routes hierarchy and content through the account's default public
    // folder mailbox.
    private static readonly PropertySet RemoteFolderPropertySet = new(
        BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass, FolderSchema.ChildFolderCount);

    private const int RemoteFolderChildPageSize = 1000;

    public override bool SupportsPublicFolders => true;

    public override async Task<IReadOnlyList<PublicFolderNode>> GetPublicFolderChildrenAsync(string parentFolderId, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var view = new FolderView(RemoteFolderChildPageSize) { Traversal = FolderTraversal.Shallow, PropertySet = RemoteFolderPropertySet };

        var results = string.IsNullOrEmpty(parentFolderId)
            ? await service.FindFolders(WellKnownFolderName.PublicFoldersRoot, view).ConfigureAwait(false)
            : await service.FindFolders(new FolderId(parentFolderId), view).ConfigureAwait(false);

        return MapRemoteFolderNodes(results.Folders, parentFolderId);
    }

    public override Task<IReadOnlyList<MailCopy>> GetPublicFolderMailItemsAsync(string folderId, int skip, int take, CancellationToken cancellationToken = default)
        => FetchRemoteFolderMailItemsAsync(folderId, skip, take);

    public override Task<byte[]> GetPublicFolderMailMimeAsync(string folderId, string itemId, CancellationToken cancellationToken = default)
        => FetchRemoteFolderMailMimeAsync(itemId);

    public override async Task<IReadOnlyList<CalendarItem>> GetPublicFolderAppointmentsAsync(string folderId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync(TimeZoneInfo.Utc).ConfigureAwait(false);
        var calendarFolder = await CalendarFolder.Bind(service, new FolderId(folderId), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);

        // CalendarView expands recurrence into individual occurrences, so each appointment maps to one event.
        var view = new CalendarView(startUtc, endUtc, RemoteFolderChildPageSize) { PropertySet = new PropertySet(BasePropertySet.IdOnly) };
        var results = await calendarFolder.FindAppointments(view).ConfigureAwait(false);
        var appointments = results.Items;

        if (appointments.Count == 0)
            return Array.Empty<CalendarItem>();

        await service.LoadPropertiesForItems(appointments, EventPropertySet).ConfigureAwait(false);
        return appointments.Select(a => MapRemoteAppointment(MapToSyncedEvent(a))).ToList();
    }

    public override async Task<IReadOnlyList<PublicFolderContact>> GetPublicFolderContactsAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var contacts = new List<PublicFolderContact>();

        var view = new ItemView(ContactDownloadPageSize);
        FindItemsResults<Item> results;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            results = await service.FindItems(new FolderId(folderId), view).ConfigureAwait(false);

            var page = results.Items.OfType<Contact>().ToList();
            if (page.Count > 0)
            {
                await service.LoadPropertiesForItems(page, ContactPropertySet).ConfigureAwait(false);
                contacts.AddRange(page.Select(MapRemoteContact).Where(c => c != null));
            }

            view.Offset += results.Items.Count;
        }
        while (results.MoreAvailable);

        return contacts.OrderBy(c => c.DisplayName ?? c.Address, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// A transient copy of a remote (public folder or archive) item. FolderId is left empty: the mail list
    /// stamps the synthetic navigation folder's id so the item resolves its folder and account from the
    /// list's own folders. The file id is derived from the item id so a re-open hits the MIME cache.
    /// </summary>
    private MailCopy MapRemoteMailItem(Item item)
    {
        if (item?.Id == null)
            return null;

        var email = item as EmailMessage;
        return new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = RemoteItemFileId(item.Id.UniqueId),
            Id = item.Id.UniqueId,
            FolderId = Guid.Empty,
            ThreadId = SafeGet(() => item.ConversationId?.UniqueId),
            MessageId = SafeGet(() => email?.InternetMessageId),
            Subject = SafeGet(() => item.Subject),
            FromName = SafeGet(() => email?.From?.Name),
            FromAddress = SafeGet(() => email?.From?.Address),
            CreationDate = SafeGet(() => item.DateTimeReceived.ToUniversalTime()),
            IsRead = true,                     // read state is not per-user-meaningful in a shared store
            IsFlagged = SafeGet(() => item.Flag?.FlagStatus) == ItemFlagStatus.Flagged,
            HasAttachments = SafeGet(() => (bool?)item.HasAttachments) ?? false,
            Importance = MapImportance(SafeGet(() => (Importance?)item.Importance) ?? Microsoft.Exchange.WebServices.Data.Importance.Normal),
            ItemType = MapItemType(SafeGet(() => item.ItemClass)),
            AssignedAccount = Account
        };
    }

    /// <summary>A stable file id for a remote item's cached MIME, derived from the account and the item id.</summary>
    protected Guid RemoteItemFileId(string itemId)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Account.Id.ToString("N") + ":" + itemId);
        return new Guid(System.Security.Cryptography.MD5.HashData(bytes));
    }

    /// <summary>A transient, locked calendar item for a read-only overlay; the caller stamps the calendar id.</summary>
    protected static CalendarItem MapRemoteAppointment(SyncedCalendarEvent occurrence)
        => new()
        {
            Id = Guid.NewGuid(),
            RemoteEventId = occurrence.RemoteId,
            Title = occurrence.Title,
            Description = occurrence.Description,
            Location = occurrence.Location,
            StartDate = occurrence.StartUtc,
            DurationInSeconds = (occurrence.EndUtc - occurrence.StartUtc).TotalSeconds,
            StartTimeZone = TimeZoneInfo.Utc.Id,
            EndTimeZone = TimeZoneInfo.Utc.Id,
            OrganizerEmail = occurrence.OrganizerEmail,
            OrganizerDisplayName = occurrence.OrganizerName,
            IsLocked = true,
            ShowAs = occurrence.ShowAs,
            Visibility = occurrence.Visibility,
            Status = CalendarItemStatus.Accepted
        };

    private static PublicFolderContact MapRemoteContact(Contact contact)
    {
        if (contact?.Id == null)
            return null;

        return new PublicFolderContact
        {
            RemoteId = contact.Id.UniqueId,
            DisplayName = SafeGet(() => contact.DisplayName),
            Address = GetRemoteContactPrimaryEmail(contact),
            Company = SafeGet(() => contact.CompanyName),
            Title = SafeGet(() => contact.JobTitle),
            BusinessPhone = GetRemoteContactPhone(contact, PhoneNumberKey.BusinessPhone),
            HomePhone = GetRemoteContactPhone(contact, PhoneNumberKey.HomePhone),
            MobilePhone = GetRemoteContactPhone(contact, PhoneNumberKey.MobilePhone),
            BusinessFax = GetRemoteContactPhone(contact, PhoneNumberKey.BusinessFax),
            StreetAddress = SafeGet(() => contact.PhysicalAddresses.TryGetValue(PhysicalAddressKey.Business, out var a) ? a?.Street : null),
            Notes = SafeGet(() => contact.Body?.Text)
        };
    }

    private static string GetRemoteContactPrimaryEmail(Contact contact)
    {
        foreach (var key in new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 })
        {
            var address = SafeGet(() => contact.EmailAddresses.TryGetValue(key, out var e) ? e?.Address : null);
            if (!string.IsNullOrWhiteSpace(address))
                return address;
        }

        return null;
    }

    private static string GetRemoteContactPhone(Contact contact, PhoneNumberKey key)
        => SafeGet(() => contact.PhoneNumbers.TryGetValue(key, out var v) ? v : null);

    // Projects EWS folder results into ordered transient nodes, trusting each folder's container class
    // exactly as Outlook does. Shared by the public folder and online archive browse paths.
    private static List<PublicFolderNode> MapRemoteFolderNodes(IEnumerable<Folder> folders, string parentFolderId)
        => folders
            .Select(f => new PublicFolderNode
            {
                Id = f.Id.UniqueId,
                ParentId = parentFolderId,
                Name = string.IsNullOrWhiteSpace(f.DisplayName) ? Translator.RemoteFolders_Unnamed : f.DisplayName,
                Kind = PublicFolderClassifier.Classify(f.FolderClass),
                HasChildren = f.ChildFolderCount > 0
            })
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // Shared live (never persisted) reads used by both browse paths: a folder's items by page, and a
    // single item's MIME. Both fetch by folder or item id off a fresh service.
    private async Task<IReadOnlyList<MailCopy>> FetchRemoteFolderMailItemsAsync(string folderId, int skip, int take)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var pageSize = take <= 0 ? (int)InitialMessageDownloadCountPerFolder : take;
        var view = new ItemView(pageSize, Math.Max(0, skip)) { PropertySet = ItemMetadataPropertySet };

        // Consecutive skip/take windows only line up under a fixed order; newest first matches the list.
        view.OrderBy.Add(ItemSchema.DateTimeReceived, SortDirection.Descending);

        var results = await service.FindItems(new FolderId(folderId), view).ConfigureAwait(false);
        return results.Items.Select(MapRemoteMailItem).Where(m => m != null).ToList();
    }

    private async Task<byte[]> FetchRemoteFolderMailMimeAsync(string itemId)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var item = await Item.Bind(service, new ItemId(itemId), new PropertySet(BasePropertySet.IdOnly, ItemSchema.MimeContent)).ConfigureAwait(false);
        return item.MimeContent?.Content;
    }

    #endregion

    #region Online Archive

    // Read-only browse of the user's in-place archive mailbox via WellKnownFolderName.ArchiveMsgFolderRoot
    // (the archive analogue of MsgFolderRoot). Like public folders, everything is fetched live and mapped
    // to transient copies; archive folders are mail folders, so they reuse the public folder classifier,
    // mapping and property sets. When no archive is provisioned the root call returns null so the UI
    // shows "not enabled" rather than a load error.

    public override bool SupportsOnlineArchive => true;

    public override async Task<IReadOnlyList<PublicFolderNode>> GetOnlineArchiveChildrenAsync(string parentFolderId, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var view = new FolderView(RemoteFolderChildPageSize) { Traversal = FolderTraversal.Shallow, PropertySet = RemoteFolderPropertySet };

        FindFoldersResults results;
        try
        {
            results = string.IsNullOrEmpty(parentFolderId)
                ? await service.FindFolders(WellKnownFolderName.ArchiveMsgFolderRoot, view).ConfigureAwait(false)
                : await service.FindFolders(new FolderId(parentFolderId), view).ConfigureAwait(false);
        }
        catch (ServiceResponseException ex) when (string.IsNullOrEmpty(parentFolderId))
        {
            _logger.Debug("Online archive not available for {Account}: {ErrorCode}.", Account.Name, ex.ErrorCode);
            return null;
        }

        return MapRemoteFolderNodes(results.Folders, parentFolderId);
    }

    public override Task<IReadOnlyList<MailCopy>> GetOnlineArchiveMailItemsAsync(string folderId, int skip, int take, CancellationToken cancellationToken = default)
        => FetchRemoteFolderMailItemsAsync(folderId, skip, take);

    public override Task<byte[]> GetOnlineArchiveMailMimeAsync(string folderId, string itemId, CancellationToken cancellationToken = default)
        => FetchRemoteFolderMailMimeAsync(itemId);

    #endregion

    #region Global Address List

    public override bool SupportsGlobalAddressList => true;

    // Resolves the query against the directory (ResolveName cannot enumerate a GAL, only match) and maps
    // each ambiguous match to a transient AccountContact for recipient autocomplete. Mail-enabled
    // distribution lists resolve to their SMTP address and ride along; EX-only/legacy entries are dropped.
    public override async Task<IReadOnlyList<AccountContact>> SearchGlobalAddressListAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Array.Empty<AccountContact>();

        var service = await CreateServiceAsync().ConfigureAwait(false);
        var resolutions = await service
            .ResolveName(query.Trim(), ResolveNameSearchLocation.DirectoryOnly, returnContactDetails: true, cancellationToken)
            .ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<AccountContact>();

        foreach (var resolution in resolutions)
        {
            var address = CleanSmtpAddress(resolution.Mailbox?.Address);
            if (address == null || !seen.Add(address))
                continue;

            var contact = resolution.Contact;

            results.Add(new AccountContact
            {
                MailAccountId = Account.Id,
                SourceKind = ContactSourceKind.Exchange,
                Address = address,
                Name = ResolveDirectoryDisplayName(resolution, address),
                CompanyName = contact?.CompanyName,
                JobTitle = contact?.JobTitle,
                Department = contact?.Department,
            });

            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    // Picks a friendly display name for a resolved directory entry: the contact's display name, else the
    // mailbox name, else the supplied fallback (the SMTP address).
    private static string ResolveDirectoryDisplayName(NameResolution resolution, string fallback)
    {
        var name = resolution?.Contact?.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
            name = resolution?.Mailbox?.Name;

        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    #endregion

    #region Inbox rules

    // Server-side inbox rules are a direct request/response (the manager dialog reads them and reports
    // save success synchronously), not a queued mutation. The EWS NETCore signatures take the
    // CancellationToken LAST: GetInboxRules(ct) and UpdateInboxRules(operations, removeOutlookRuleBlob, ct).
    public override bool SupportsInboxRules => true;

    public override async Task<IReadOnlyList<RemoteInboxRule>> GetInboxRulesAsync(CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);
        var ruleCollection = await service.GetInboxRules(cancellationToken).ConfigureAwait(false);

        var rules = new List<RemoteInboxRule>();
        foreach (var rule in ruleCollection)
            rules.Add(ExchangeRuleMapper.ToDto(rule));

        // EWS returns internal recipients as legacy X500/EX distinguished names; resolve them to SMTP so
        // the UI shows a readable address and run-now matches exactly. Best-effort + cached per read; the
        // DN is kept on failure (run-now still matches it by the sender's display name).
        var smtpCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in rules)
        {
            foreach (var condition in dto.Conditions.Where(c => c.Field is RuleConditionField.From or RuleConditionField.SentTo))
                condition.Value = await ResolveAddressListToSmtpAsync(service, condition.Value, smtpCache, cancellationToken).ConfigureAwait(false);

            foreach (var action in dto.Actions.Where(a => a.Type == RuleActionType.Forward))
                action.Value = await ResolveAddressListToSmtpAsync(service, action.Value, smtpCache, cancellationToken).ConfigureAwait(false);
        }

        return rules.OrderBy(r => r.Priority).ToList();
    }

    private static bool IsExchangeLegacyDn(string value)
        => !string.IsNullOrEmpty(value)
           && (value.StartsWith("/o=", StringComparison.OrdinalIgnoreCase) || value.Contains("/cn=", StringComparison.OrdinalIgnoreCase));

    private static string StripSmtpPrefix(string address)
        => address != null && address.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? address.Substring(5) : address;

    // Rewrites a comma/semicolon-separated address list, replacing any legacy EX DN with its SMTP address.
    private async Task<string> ResolveAddressListToSmtpAsync(ExchangeService service, string value, IDictionary<string, string> cache, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsExchangeLegacyDn(value))
            return value;

        var resolved = new List<string>();
        foreach (var part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            resolved.Add(IsExchangeLegacyDn(trimmed)
                ? await ResolveLegacyDnToSmtpAsync(service, trimmed, cache, cancellationToken).ConfigureAwait(false) ?? trimmed
                : trimmed);
        }

        return string.Join(", ", resolved);
    }

    private async Task<string> ResolveLegacyDnToSmtpAsync(ExchangeService service, string dn, IDictionary<string, string> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(dn, out var cached))
            return cached;

        string smtp = null;
        try
        {
            var resolutions = await service
                .ResolveName(dn, ResolveNameSearchLocation.DirectoryOnly, returnContactDetails: true, cancellationToken)
                .ConfigureAwait(false);

            var resolution = resolutions.FirstOrDefault();
            if (resolution != null)
            {
                var mailbox = resolution.Mailbox?.Address;
                if (!string.IsNullOrEmpty(mailbox) && mailbox.Contains('@'))
                {
                    smtp = StripSmtpPrefix(mailbox);
                }
                else if (resolution.Contact?.EmailAddresses != null)
                {
                    foreach (var key in new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 })
                    {
                        if (resolution.Contact.EmailAddresses.TryGetValue(key, out var emailAddress)
                            && emailAddress?.Address?.Contains('@') == true)
                        {
                            smtp = StripSmtpPrefix(emailAddress.Address);
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not resolve EX address to SMTP for {Account}.", Account.Name);
        }

        cache[dn] = smtp;
        return smtp;
    }

    public override async Task<InboxRuleUpdateResult> UpdateInboxRulesAsync(IReadOnlyList<InboxRuleChange> changes, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default)
    {
        if (changes == null || changes.Count == 0)
            return InboxRuleUpdateResult.Ok;

        var operations = new List<RuleOperation>();
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case InboxRuleChangeKind.Create:
                    operations.Add(new CreateRuleOperation(ExchangeRuleMapper.ToEwsRule(change.Rule)));
                    break;
                case InboxRuleChangeKind.Update:
                    operations.Add(new SetRuleOperation(ExchangeRuleMapper.ToEwsRule(change.Rule)));
                    break;
                case InboxRuleChangeKind.Delete:
                    operations.Add(new DeleteRuleOperation(change.RuleId));
                    break;
            }
        }

        if (operations.Count == 0)
            return InboxRuleUpdateResult.Ok;

        var service = await CreateServiceAsync().ConfigureAwait(false);

        try
        {
            // The Outlook rule blob is only removed with explicit user consent: when a mailbox's rules
            // were last managed by classic Outlook, EWS rejects updates (ErrorOutlookRuleBlobExists)
            // until the blob is cleared. Callers surface that as a consent prompt and retry with true.
            await service.UpdateInboxRules(operations, removeOutlookRuleBlob, cancellationToken).ConfigureAwait(false);
            return InboxRuleUpdateResult.Ok;
        }
        catch (UpdateInboxRulesException ex) when (ex.ErrorCode == ServiceError.ErrorOutlookRuleBlobExists)
        {
            _logger.Warning("UpdateInboxRules blocked by the classic Outlook rule blob for {Account}.", Account.Name);
            return new InboxRuleUpdateResult
            {
                Success = false,
                Errors = new[] { Translator.Rules_OutlookBlobExists },
                RequiresOutlookRuleBlobRemoval = true
            };
        }
        catch (UpdateInboxRulesException ex)
        {
            _logger.Warning(ex, "UpdateInboxRules reported errors for {Account}: {Code} {Message}", Account.Name, ex.ErrorCode, ex.ErrorMessage);
            var message = string.IsNullOrWhiteSpace(ex.ErrorMessage) ? ex.ErrorCode.ToString() : ex.ErrorMessage;
            return InboxRuleUpdateResult.Failed(message);
        }
    }

    #endregion

    public override List<IRequestBundle<EwsRequest>> Delete(BatchDeleteRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(
            service => service.DeleteItems(ids, DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences),
            requests[0], requests);
    }

    // Archive moves to the local Archive special folder (Exchange has no archive action of its own on the
    // primary mailbox; the in-place archive mailbox is populated server-side by retention policy).
    public override List<IRequestBundle<EwsRequest>> Archive(BatchArchiveRequest request)
        => Move(new BatchMoveRequest(request.Select(a => new MoveRequest(a.Item, a.FromFolder, a.ToFolder))));

    public override List<IRequestBundle<EwsRequest>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a))));

    public override List<IRequestBundle<EwsRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    public override List<IRequestBundle<EwsRequest>> SendDraft(SendDraftRequest request)
    {
        var preparation = request.Request;

        return Bundle(async service =>
        {
            var mime = preparation.Mime;

            // Strip the local-draft marker so it never leaks to recipients.
            mime.Headers.Remove(Domain.Constants.WinoLocalDraftHeader);

            using var stream = new MemoryStream();
            await mime.WriteToAsync(stream).ConfigureAwait(false);

            var message = new EmailMessage(service)
            {
                MimeContent = new Microsoft.Exchange.WebServices.Data.MimeContent("UTF-8", stream.ToArray())
            };

            // On-prem Exchange cannot send as proxy aliases; let transport use the mailbox primary.
            if (preparation.SentFolder != null)
                await message.SendAndSaveCopy(new FolderId(preparation.SentFolder.RemoteFolderId)).ConfigureAwait(false);
            else
                await message.SendAndSaveCopy().ConfigureAwait(false);

            // Best-effort cleanup of the server draft created by CreateDraft.
            var serverDraftId = preparation.MailItem?.Id;
            if (!string.IsNullOrWhiteSpace(serverDraftId) && !(preparation.MailItem?.IsLocalDraft ?? true))
            {
                try
                {
                    await service.DeleteItems(
                        [new ItemId(serverDraftId)],
                        DeleteMode.HardDelete,
                        SendCancellationsMode.SendToNone,
                        AffectedTaskOccurrence.AllOccurrences).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not delete server draft {DraftId} after send (it may already be gone).", serverDraftId);
                }
            }
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> CreateDraft(CreateDraftRequest request)
    {
        var preparation = request.DraftPreperationRequest;
        var draftsFolderId = preparation.CreatedLocalDraftCopy.AssignedFolder.RemoteFolderId;

        return Bundle(async service =>
        {
            using var stream = new MemoryStream();
            await preparation.CreatedLocalDraftMimeMessage.WriteToAsync(stream).ConfigureAwait(false);

            var message = new EmailMessage(service)
            {
                MimeContent = new Microsoft.Exchange.WebServices.Data.MimeContent("UTF-8", stream.ToArray())
            };

            await message.Save(new FolderId(draftsFolderId)).ConfigureAwait(false);

            var isMapped = await _exchangeChangeProcessor.MapLocalDraftAsync(
                Account.Id,
                preparation.CreatedLocalDraftCopy.UniqueId,
                message.Id.UniqueId,
                message.Id.UniqueId,
                preparation.CreatedLocalDraftCopy.ThreadId).ConfigureAwait(false);

            if (!isMapped)
            {
                // The local draft was discarded while the EWS save was in flight. Delete the
                // server draft right away so the next Drafts sync cannot resurrect it.
                await service.DeleteItems(
                    [message.Id],
                    DeleteMode.HardDelete,
                    SendCancellationsMode.SendToNone,
                    AffectedTaskOccurrence.AllOccurrences).ConfigureAwait(false);
            }
        }, request, request);
    }

    protected override Task MarkDraftSyncFailedAsync(Guid mailUniqueId, string error)
        => _exchangeChangeProcessor.MarkDraftSyncFailedAsync(mailUniqueId, error);

    /// <summary>
    /// A later save of a server draft. EWS accepts MimeContent only on creation, so the draft is
    /// replaced: the new MIME is saved beside the old item, the old one is removed, and the new id is
    /// handed back so the local row follows it (the draft update coordinator dedupes by id and folder).
    /// </summary>
    public override async Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot, MailCopy draft, CancellationToken cancellationToken = default)
    {
        var draftsFolderId = draft.AssignedFolder?.RemoteFolderId
            ?? throw new InvalidOperationException("The draft has no remote folder to be saved into.");

        var service = await CreateServiceAsync().ConfigureAwait(false);

        using var mime = snapshot.OpenMime();
        using var stream = new MemoryStream();
        await mime.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);

        var replacement = new EmailMessage(service)
        {
            MimeContent = new Microsoft.Exchange.WebServices.Data.MimeContent("UTF-8", stream.ToArray())
        };

        await replacement.Save(new FolderId(draftsFolderId)).ConfigureAwait(false);

        try
        {
            await service.DeleteItems(
                [new ItemId(draft.Id)],
                DeleteMode.HardDelete,
                SendCancellationsMode.SendToNone,
                AffectedTaskOccurrence.AllOccurrences).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The old draft is superseded either way; the next Drafts sync removes a leftover.
            _logger.Debug(ex, "Could not delete the superseded server draft {DraftId}.", draft.Id);
        }

        return new DraftUpdateIdentity(replacement.Id.UniqueId, replacement.Id.UniqueId, draft.ThreadId);
    }

    #endregion

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<EwsRequest>> batchedRequests, CancellationToken cancellationToken = default)
    {
        if (batchedRequests == null || batchedRequests.Count == 0)
            return;

        ApplyOptimisticUiChanges(batchedRequests);

        var service = await CreateServiceAsync().ConfigureAwait(false);
        var errors = new List<string>();

        for (int i = 0; i < batchedRequests.Count; i++)
        {
            var bundle = batchedRequests[i];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await bundle.NativeRequest.IntegratorTask(service, bundle.NativeRequest.Request).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled before/while executing this bundle: its optimistic UI change, and those of every
                // bundle after it, was applied up front but never confirmed by the server, so revert them. The
                // bundles before i already executed successfully and stay applied.
                for (int j = i; j < batchedRequests.Count; j++)
                    RequestUiChangeCoordinator.RevertBundle(batchedRequests[j]);

                throw;
            }
            catch (Exception ex)
            {
                await HandleFailedRequestAsync(bundle, ex, errors).ConfigureAwait(false);
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private async Task HandleFailedRequestAsync(IRequestBundle<EwsRequest> bundle, Exception exception, List<string> errors)
    {
        if (bundle.Request is CreateDraftRequest createDraftRequest)
        {
            await _exchangeChangeProcessor
                .MarkDraftSyncFailedAsync(createDraftRequest.Item.UniqueId, exception.Message)
                .ConfigureAwait(false);
        }

        var errorContext = new SynchronizerErrorContext
        {
            Account = Account,
            ErrorMessage = exception.Message,
            Exception = exception,
            RequestBundle = bundle,
            Request = bundle.Request,
            IsEntityNotFound = IsEwsEntityNotFound(exception, bundle.UIChangeRequest),
            OperationType = "ExchangeExecuteRequest"
        };

        var handled = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

        // Handled non-transient errors are owned by their handlers.
        if (!handled || errorContext.Severity == SynchronizerErrorSeverity.Transient)
        {
            CaptureSynchronizationIssue(errorContext);
            RequestUiChangeCoordinator.RevertBundle(bundle);
            _logger.Error(exception, "Exchange request execution failed for {Account}.", Account.Name);
            errors.Add(exception.Message);
        }
    }

    private static bool IsEwsEntityNotFound(Exception exception, IUIChangeRequest uiChangeRequest)
    {
        if (uiChangeRequest == null || !IsExistingEntityOperation(uiChangeRequest))
            return false;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is ServiceResponseException serviceResponse &&
                serviceResponse.ErrorCode is ServiceError.ErrorItemNotFound
                    or ServiceError.ErrorFolderNotFound
                    or ServiceError.ErrorNonExistentMailbox)
            {
                return true;
            }

            var message = current.Message?.ToLowerInvariant() ?? string.Empty;
            if (message.Contains("not found") || message.Contains("does not exist") || message.Contains("cannot be found"))
                return true;
        }

        return false;
    }

    private static bool IsExistingEntityOperation(IUIChangeRequest request)
        => request is BatchDeleteRequest or BatchMoveRequest or BatchChangeFlagRequest
            or BatchMarkReadRequest or BatchArchiveRequest
            or DeleteRequest or MoveRequest or ChangeFlagRequest or MarkReadRequest;
}

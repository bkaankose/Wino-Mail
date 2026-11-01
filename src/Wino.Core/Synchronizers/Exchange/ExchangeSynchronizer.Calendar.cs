using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Misc;
using Wino.Core.Requests;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

public partial class ExchangeSynchronizer
{
    protected const int CalendarWindowPastMonths = 3;
    protected const int CalendarWindowFutureMonths = 12;

    // The IPF class of calendar folders.
    private const string CalendarFolderClass = "IPF.Appointment";

    protected bool IsProviderCalendarAvailable
        => Account.CalendarIntegrationSource == AccountIntegrationSource.Provider && Account.IsCalendarAccessGranted;

    /// <summary>
    /// The server calendars that take part in this pass: every enabled one, or only the ones a single-calendar request
    /// names. A calendar without a remote id is a local calendar the account kept from before; it is never synced.
    /// </summary>
    protected async Task<List<AccountCalendar>> GetCalendarsToSynchronizeAsync(CalendarSynchronizationOptions options)
    {
        var calendars = (await ExchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false))
            .Where(c => c.IsSynchronizationEnabled && !string.IsNullOrWhiteSpace(c.RemoteCalendarId))
            .ToList();

        if (options?.Type == CalendarSynchronizationType.SingleCalendar && options.SynchronizationCalendarIds is { Count: > 0 } ids)
            calendars = calendars.Where(c => ids.Contains(c.Id)).ToList();

        return calendars;
    }

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
            return CalendarSynchronizationResult.Empty;

        if (!IsProviderCalendarAvailable)
            return CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable));

        try
        {
            var service = await CreateServiceAsync(cancellationToken, TimeZoneInfo.Utc).ConfigureAwait(false);

            await SynchronizeCalendarsAsync(service, cancellationToken).ConfigureAwait(false);

            // Exchange has no change feed for events outside push notifications, and the window read is cheap, so every
            // pass refreshes the events as well; otherwise they would arrive only on a manual sync.
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
            _logger.Error(ex, "EWS calendar synchronization failed for {Account}.", Account.Name);
            return CalendarSynchronizationResult.Failed(ex);
        }
    }

    private async Task SynchronizeCalendarsAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var properties = new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass);

        var defaultCalendar = await CalendarFolder.Bind(service, WellKnownFolderName.Calendar, properties).ConfigureAwait(false);

        var view = new FolderView(1000) { Traversal = FolderTraversal.Deep, PropertySet = properties };
        var appointmentFolders = await service
            .FindFolders(WellKnownFolderName.MsgFolderRoot, new SearchFilter.IsEqualTo(FolderSchema.FolderClass, CalendarFolderClass), view)
            .ConfigureAwait(false);

        var remoteFolders = new Dictionary<string, Folder>(StringComparer.Ordinal) { [defaultCalendar.Id.UniqueId] = defaultCalendar };
        foreach (var folder in appointmentFolders.Folders)
            remoteFolders[folder.Id.UniqueId] = folder;

        var localCalendars = await ExchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        foreach (var local in localCalendars)
        {
            if (!string.IsNullOrWhiteSpace(local.RemoteCalendarId) && !remoteFolders.ContainsKey(local.RemoteCalendarId))
                await ExchangeChangeProcessor.DeleteAccountCalendarAsync(local).ConfigureAwait(false);
        }

        foreach (var (remoteId, folder) in remoteFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isPrimary = remoteId == defaultCalendar.Id.UniqueId;
            var name = string.IsNullOrWhiteSpace(folder.DisplayName) ? Translator.AccountDetailsPage_TabCalendar : folder.DisplayName;

            var existing = localCalendars.FirstOrDefault(c => c.RemoteCalendarId == remoteId);
            if (existing == null)
            {
                await ExchangeChangeProcessor.InsertAccountCalendarAsync(BuildAccountCalendar(remoteId, name, isPrimary)).ConfigureAwait(false);
            }
            else if (existing.Name != name || existing.IsPrimary != isPrimary)
            {
                existing.Name = name;
                existing.IsPrimary = isPrimary;
                await ExchangeChangeProcessor.UpdateAccountCalendarAsync(existing).ConfigureAwait(false);
            }
        }
    }

    protected AccountCalendar BuildAccountCalendar(string remoteCalendarId, string name, bool isPrimary)
    {
        var backgroundColor = string.IsNullOrWhiteSpace(Account.AccountColorHex) ? "#2564CF" : Account.AccountColorHex;

        return new()
        {
            Id = Guid.NewGuid(),
            AccountId = Account.Id,
            RemoteCalendarId = remoteCalendarId,
            Name = name,
            IsPrimary = isPrimary,
            IsSynchronizationEnabled = true,
            IsReadOnly = false,
            DefaultShowAs = CalendarItemShowAs.Busy,
            BackgroundColorHex = backgroundColor,
            TextColorHex = ColorHelpers.GetReadableTextColorHex(backgroundColor),
            TimeZone = TimeZoneInfo.Local.Id
        };
    }

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

        var seenRemoteIds = new HashSet<string>(StringComparer.Ordinal);

        // Deletions are reconciled only when no chunk hit the server's result cap.
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
                    await ExchangeChangeProcessor.ManageCalendarEventAsync(MapToSyncedEvent(appointment), calendar, Account).ConfigureAwait(false);
                }
            }

            if (results.MoreAvailable)
            {
                windowComplete = false;
                _logger.Warning("Calendar window chunk for {Calendar} hit the result cap; deletions are not reconciled this pass.", calendar.Name);
            }

            chunkStartUtc = chunkEndUtc;
        }

        if (!windowComplete)
            return;

        var localEvents = await ExchangeChangeProcessor.GetCalendarItemsInRangeAsync(calendar, windowStartUtc, windowEndUtc).ConfigureAwait(false);
        foreach (var local in localEvents)
        {
            if (!string.IsNullOrEmpty(local.RemoteEventId) &&
                !seenRemoteIds.Contains(local.RemoteEventId.GetProviderRemoteEventId()))
            {
                await ExchangeChangeProcessor.DeleteCalendarItemAsync(local.Id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>An EWS appointment (one occurrence; the calendar view expands series) in the neutral shape.</summary>
    private static SyncedCalendarEvent MapToSyncedEvent(Appointment appointment)
    {
        Guid? clientTrackingId = null;
        if (appointment.TryGetProperty(ExchangeCalendarSchema.WinoClientTrackingId, out string trackingValue) &&
            Guid.TryParseExact(trackingValue, "N", out var parsedTrackingId))
        {
            clientTrackingId = parsedTrackingId;
        }

        string? ianaTimeZone = null;
        var startTimeZoneId = SafeGet(() => appointment.StartTimeZone?.Id);
        if (!string.IsNullOrEmpty(startTimeZoneId) && TimeZoneInfo.TryConvertWindowsIdToIanaId(startTimeZoneId, out var iana))
            ianaTimeZone = iana;

        var isReminderSet = SafeGet(() => (bool?)appointment.IsReminderSet) ?? false;

        return new SyncedCalendarEvent
        {
            RemoteId = appointment.Id.UniqueId.WithClientTrackingId(clientTrackingId),
            Title = SafeGet(() => appointment.Subject),
            Description = SafeGet(() => appointment.Body?.Text),
            Location = SafeGet(() => appointment.Location),
            StartUtc = DateTime.SpecifyKind(appointment.Start, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(appointment.End, DateTimeKind.Utc),
            TimeZoneIana = ianaTimeZone,
            IsAllDay = SafeGet(() => (bool?)appointment.IsAllDayEvent) ?? false,
            OrganizerEmail = SafeGet(() => appointment.Organizer?.Address),
            OrganizerName = SafeGet(() => appointment.Organizer?.Name),
            CreatedAt = SafeGet(() => (DateTime?)appointment.DateTimeCreated) ?? DateTime.UtcNow,
            UpdatedAt = SafeGet(() => (DateTime?)appointment.LastModifiedTime) ?? DateTime.UtcNow,
            Visibility = SafeGet(() => (Sensitivity?)appointment.Sensitivity) switch
            {
                Sensitivity.Personal or Sensitivity.Private => CalendarItemVisibility.Private,
                Sensitivity.Confidential => CalendarItemVisibility.Confidential,
                _ => CalendarItemVisibility.Public
            },
            ShowAs = SafeGet(() => (LegacyFreeBusyStatus?)appointment.LegacyFreeBusyStatus) switch
            {
                LegacyFreeBusyStatus.Free => CalendarItemShowAs.Free,
                LegacyFreeBusyStatus.Tentative => CalendarItemShowAs.Tentative,
                LegacyFreeBusyStatus.OOF => CalendarItemShowAs.OutOfOffice,
                LegacyFreeBusyStatus.WorkingElsewhere => CalendarItemShowAs.WorkingElsewhere,
                _ => CalendarItemShowAs.Busy
            },
            MyResponse = SafeGet(() => (MeetingResponseType?)appointment.MyResponseType) switch
            {
                MeetingResponseType.Tentative => CalendarItemStatus.Tentative,
                MeetingResponseType.Decline => CalendarItemStatus.Cancelled,
                MeetingResponseType.NoResponseReceived => CalendarItemStatus.NotResponded,
                _ => CalendarItemStatus.Accepted
            },
            Attendees = BuildAttendees(appointment),
            ReminderMinutesBeforeStart = isReminderSet ? SafeGet(() => (int?)appointment.ReminderMinutesBeforeStart) : null
        };
    }

    private static List<SyncedCalendarAttendee>? BuildAttendees(Appointment appointment)
    {
        var attendees = new List<SyncedCalendarAttendee>();
        AddAttendees(attendees, SafeGet(() => appointment.RequiredAttendees), isOptional: false);
        AddAttendees(attendees, SafeGet(() => appointment.OptionalAttendees), isOptional: true);
        return attendees.Count == 0 ? null : attendees;
    }

    private static void AddAttendees(List<SyncedCalendarAttendee> target, AttendeeCollection? source, bool isOptional)
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

    /// <summary>
    /// Links a meeting message to the calendar item it created, in the shape the Outlook synchronizer writes: the
    /// appointment is found by its iCalendar UID in each synced calendar, stored when the calendar sync has not
    /// reached it yet, and the mapping row is written. Failures are logged; the mail itself is already saved.
    /// </summary>
    private async Task TryMapCalendarInvitationAsync(ExchangeService service, MailCopy mailCopy, MimeMessage mimeMessage, CancellationToken cancellationToken)
    {
        if (!IsProviderCalendarAvailable)
            return;

        var invitationUid = mimeMessage.ExtractInvitationUid();
        if (string.IsNullOrWhiteSpace(invitationUid))
            return;

        List<AccountCalendar> calendars;
        try
        {
            calendars = await GetCalendarsToSynchronizeAsync(null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Calendars of {Account} not loaded for invitation mail {MailCopyId}.", Account.Name, mailCopy.Id);
            return;
        }

        foreach (var calendar in calendars.OrderByDescending(c => c.IsPrimary))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var view = new ItemView(1) { PropertySet = new PropertySet(BasePropertySet.IdOnly, AppointmentSchema.AppointmentType) };
                var filter = new SearchFilter.IsEqualTo(AppointmentSchema.ICalUid, invitationUid);
                var results = await service.FindItems(new FolderId(calendar.RemoteCalendarId), filter, view).ConfigureAwait(false);

                var match = results.Items.OfType<Appointment>().FirstOrDefault();
                if (match == null)
                    continue;

                // The calendar stores occurrences, so a series request maps to its first occurrence rather than the
                // master, which this transport never stores.
                var appointmentId = match.Id;
                if (SafeGet(() => (AppointmentType?)match.AppointmentType) == AppointmentType.RecurringMaster)
                {
                    var master = await Appointment.Bind(service, match.Id, new PropertySet(BasePropertySet.IdOnly, AppointmentSchema.FirstOccurrence)).ConfigureAwait(false);
                    appointmentId = SafeGet(() => master.FirstOccurrence?.ItemId);

                    if (appointmentId == null)
                        continue;
                }

                var appointment = await Appointment.Bind(service, appointmentId, EventPropertySet).ConfigureAwait(false);
                await ExchangeChangeProcessor.ManageCalendarEventAsync(MapToSyncedEvent(appointment), calendar, Account).ConfigureAwait(false);

                var localCalendarItem = await ExchangeChangeProcessor.GetCalendarItemAsync(calendar.Id, appointment.Id.UniqueId).ConfigureAwait(false);
                if (localCalendarItem == null)
                    return;

                await ExchangeChangeProcessor.UpsertMailInvitationCalendarMappingAsync(new MailInvitationCalendarMapping
                {
                    Id = Guid.NewGuid(),
                    AccountId = Account.Id,
                    MailCopyId = mailCopy.Id,
                    InvitationUid = invitationUid,
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
                _logger.Warning(ex, "Exchange invitation mail {MailCopyId} not mapped to calendar {CalendarId}.", mailCopy.Id, calendar.Id);
            }
        }
    }

    #region Calendar operations

    public override List<IRequestBundle<EwsRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var item = request.PreparedItem;
        var attendees = request.ComposeResult?.Attendees;
        var reminders = request.ComposeResult?.SelectedReminders;
        var calendarRemoteId = request.AssignedCalendar.RemoteCalendarId;
        var isRecurring = request.IsRecurring;
        var recurrenceRule = EwsRecurrenceMapper.GetRecurrenceRule(item) ?? request.ComposeResult?.Recurrence;
        var title = item.Title;

        return Bundle(async (service, _) =>
        {
            var appointment = new Appointment(service);
            ApplyCalendarItem(appointment, item, attendees, reminders);

            // The local preview id lets the synced event reconcile with the optimistic item.
            appointment.SetExtendedProperty(ExchangeCalendarSchema.WinoClientTrackingId, item.Id.ToString("N"));

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

            // The calendar view returns a series as its occurrences, so a master stored here would never be confirmed
            // by a sync; the resync after this request stores the occurrences.
            if (!isRecurring)
            {
                await ExchangeChangeProcessor.PersistCreatedCalendarEventAsync(
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

        return Bundle(async (service, _) =>
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

        return Bundle(async (service, _) =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(remoteId.GetProviderRemoteEventId()), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
            await appointment.Delete(DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> AcceptEvent(AcceptEventRequest request)
        => RespondToEvent(request, MeetingRsvp.Accept, request.Item.RemoteEventId, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> TentativeEvent(TentativeEventRequest request)
        => RespondToEvent(request, MeetingRsvp.Tentative, request.Item.RemoteEventId, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> DeclineEvent(DeclineEventRequest request)
        => RespondToEvent(request, MeetingRsvp.Decline, request.Item.RemoteEventId, request.ResponseMessage);

    private enum MeetingRsvp { Accept, Tentative, Decline }

    private List<IRequestBundle<EwsRequest>> RespondToEvent(CalendarRequestBase request, MeetingRsvp response, string remoteEventId, string? responseMessage)
    {
        return Bundle(async (service, _) =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(remoteEventId.GetProviderRemoteEventId()), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);

            if (response == MeetingRsvp.Decline)
            {
                var decline = appointment.CreateDeclineMessage();
                if (!string.IsNullOrEmpty(responseMessage))
                    decline.Body = new MessageBody(responseMessage);

                await decline.SendAndSaveCopy().ConfigureAwait(false);
                return;
            }

            var accept = appointment.CreateAcceptMessage(response == MeetingRsvp.Tentative);
            if (!string.IsNullOrEmpty(responseMessage))
                accept.Body = new MessageBody(responseMessage);

            await accept.SendAndSaveCopy().ConfigureAwait(false);
        }, request, request);
    }

    private static bool HasAttendees(Appointment appointment)
        => appointment.RequiredAttendees.Count > 0 || appointment.OptionalAttendees.Count > 0;

    private static void ApplyCalendarItem(Appointment appointment, CalendarItem item, List<CalendarEventAttendee>? attendees, List<Reminder>? reminders)
    {
        appointment.Subject = item.Title;
        appointment.Body = new MessageBody(BodyType.HTML, item.Description ?? string.Empty);
        appointment.Location = item.Location;

        // The start is wall clock in the item's zone; the appointment carries that zone so EWS reads it correctly
        // whatever the service zone is.
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

    private static TimeZoneInfo ResolveTimeZoneInfo(string? ianaTimeZone)
    {
        if (string.IsNullOrEmpty(ianaTimeZone))
            return TimeZoneInfo.Utc;

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZone, out var windowsId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out var windowsZone))
        {
            return windowsZone;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(ianaTimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    #endregion
}

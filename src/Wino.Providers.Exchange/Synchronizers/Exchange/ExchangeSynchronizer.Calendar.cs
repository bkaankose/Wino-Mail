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
using Wino.Providers.Exchange.Requests.Bundles;
using Task = System.Threading.Tasks.Task;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

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

        // Deleted Items is under the message folder root, so a deleted calendar would otherwise still be listed; it is
        // left out as deleted task folders are.
        var filter = new SearchFilter.IsEqualTo(FolderSchema.FolderClass, CalendarFolderClass);
        var discarded = (await FindEwsFoldersAsync(service, WellKnownFolderName.DeletedItems, filter, properties, cancellationToken).ConfigureAwait(false))
            .Select(folder => folder.Id.UniqueId)
            .ToHashSet(StringComparer.Ordinal);

        var remoteFolders = new Dictionary<string, Folder>(StringComparer.Ordinal) { [defaultCalendar.Id.UniqueId] = defaultCalendar };
        foreach (var folder in await FindEwsFoldersAsync(service, WellKnownFolderName.MsgFolderRoot, filter, properties, cancellationToken).ConfigureAwait(false))
        {
            if (!discarded.Contains(folder.Id.UniqueId))
                remoteFolders.TryAdd(folder.Id.UniqueId, folder);
        }

        var localCalendars = await ExchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        await RekeyCarriedCalendarsAsync(localCalendars,
            remoteFolders.Select(pair => (pair.Key, pair.Value.DisplayName ?? string.Empty, pair.Key == defaultCalendar.Id.UniqueId)).ToList(),
            cancellationToken).ConfigureAwait(false);

        foreach (var local in localCalendars)
        {
            if (!string.IsNullOrWhiteSpace(local.RemoteCalendarId) && !remoteFolders.ContainsKey(local.RemoteCalendarId))
                await ExchangeChangeProcessor.DeleteAccountCalendarAsync(local).ConfigureAwait(false);
        }

        var usedColors = await GetPrecedingAccountsCalendarColorsAsync().ConfigureAwait(false);

        foreach (var (remoteId, folder) in remoteFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isPrimary = remoteId == defaultCalendar.Id.UniqueId;
            var name = string.IsNullOrWhiteSpace(folder.DisplayName) ? Translator.AccountDetailsPage_TabCalendar : folder.DisplayName;

            await UpsertAccountCalendarAsync(localCalendars.FirstOrDefault(c => c.RemoteCalendarId == remoteId), remoteId, name, isPrimary, usedColors).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Moves the calendars another transport stored onto this transport's folders before missing calendars are removed,
    /// so a transport change keeps each calendar row (its id, colour and synchronization setting).
    /// </summary>
    protected async Task RekeyCarriedCalendarsAsync(List<AccountCalendar> localCalendars, IReadOnlyList<(string RemoteId, string Title, bool IsDefault)> incoming,
        CancellationToken cancellationToken)
    {
        var rows = localCalendars
            .Where(calendar => !string.IsNullOrWhiteSpace(calendar.RemoteCalendarId))
            .Select(calendar => (calendar.Id, calendar.RemoteCalendarId, calendar.Name ?? string.Empty, calendar.IsPrimary))
            .ToList();
        var matches = await MatchCarriedContainersAsync(rows, incoming, cancellationToken).ConfigureAwait(false);

        foreach (var calendar in localCalendars)
        {
            if (!matches.TryGetValue(calendar.Id, out var remoteId))
                continue;

            _logger.Information("Exchange calendar {Account}: re-keying calendar {CalendarId} to the current transport.", Account.Name, calendar.Id);
            await ExchangeChangeProcessor.RekeyAccountCalendarAsync(calendar, remoteId).ConfigureAwait(false);
        }
    }

    protected async Task<HashSet<string>> GetPrecedingAccountsCalendarColorsAsync()
        => new(await ExchangeChangeProcessor.GetPrecedingAccountsCalendarColorsAsync(Account.Id).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);

    protected async Task UpsertAccountCalendarAsync(AccountCalendar existing, string remoteId, string name, bool isPrimary, HashSet<string> usedColors)
    {
        if (existing == null)
        {
            var calendar = BuildAccountCalendar(remoteId, name, isPrimary, usedColors);
            await ExchangeChangeProcessor.InsertAccountCalendarAsync(calendar).ConfigureAwait(false);
            usedColors.Add(calendar.BackgroundColorHex);
            return;
        }

        var color = ColorHelpers.ResolveDistinctCalendarColor(null, existing, usedColors);
        if (existing.Name != name || existing.IsPrimary != isPrimary || !string.Equals(existing.BackgroundColorHex, color, StringComparison.OrdinalIgnoreCase))
        {
            existing.Name = name;
            existing.IsPrimary = isPrimary;
            existing.BackgroundColorHex = color;
            existing.TextColorHex = ColorHelpers.GetReadableTextColorHex(color);
            await ExchangeChangeProcessor.UpdateAccountCalendarAsync(existing).ConfigureAwait(false);
        }

        usedColors.Add(existing.BackgroundColorHex);
    }

    private AccountCalendar BuildAccountCalendar(string remoteCalendarId, string name, bool isPrimary, HashSet<string> usedColors)
    {
        var backgroundColor = ColorHelpers.ResolveDistinctCalendarColor(null, null, usedColors);

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
        AppointmentSchema.AppointmentType,
        ItemSchema.Attachments,
        ExchangeCalendarSchema.WinoClientTrackingId)
    { RequestedBodyType = BodyType.HTML };

    private static readonly PropertySet MasterPropertySet = new(EventPropertySet) { AppointmentSchema.Recurrence };

    private async Task SynchronizeCalendarEventsForCalendarAsync(ExchangeService service, AccountCalendar calendar, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
    {
        var calendarFolder = await CalendarFolder
            .Bind(service, new FolderId(calendar.RemoteCalendarId), new PropertySet(BasePropertySet.IdOnly))
            .ConfigureAwait(false);

        var seenRemoteIds = new HashSet<string>(StringComparer.Ordinal);

        // Series masters stored in this pass by their EWS id, with the remote id their occurrences link to; null when
        // the master's pattern has no RRULE form and its occurrences stay unlinked.
        var storedMasters = new Dictionary<string, string?>(StringComparer.Ordinal);

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

                var masterIds = await StoreRecurringMastersAsync(service, calendar, appointments, storedMasters, seenRemoteIds, cancellationToken).ConfigureAwait(false);

                foreach (var appointment in appointments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    seenRemoteIds.Add(appointment.Id.UniqueId);

                    var occurrence = MapToSyncedEvent(appointment);
                    if (masterIds.TryGetValue(appointment.Id.UniqueId, out var masterId) && storedMasters.TryGetValue(masterId, out var masterRemoteId))
                        occurrence.RecurringParentRemoteId = masterRemoteId;

                    await ExchangeChangeProcessor.ManageCalendarEventAsync(occurrence, calendar, Account).ConfigureAwait(false);
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

        // Masters are outside the range query; a series gone from the server, or with no occurrence left in the window,
        // takes its stored occurrences with it.
        foreach (var master in await ExchangeChangeProcessor.GetRecurringMastersAsync(calendar).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(master.RemoteEventId) &&
                !seenRemoteIds.Contains(master.RemoteEventId.GetProviderRemoteEventId()))
            {
                await ExchangeChangeProcessor.DeleteCalendarItemAsync(master.Id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Stores the series master of every occurrence and exception in a calendar view chunk, each master read once per
    /// pass. The calendar view never returns masters, so each occurrence's master id is resolved through
    /// RecurringMasterItemId in one batched GetItem (MS-OXWSCORE, MS-OXWSMTGS), then the masters not stored yet are
    /// read in a second one. Returns the master id of each occurrence by the occurrence's id.
    /// </summary>
    private async Task<Dictionary<string, string>> StoreRecurringMastersAsync(ExchangeService service, AccountCalendar calendar, IReadOnlyList<Appointment> appointments,
        Dictionary<string, string?> storedMasters, HashSet<string> seenRemoteIds, CancellationToken cancellationToken)
    {
        var masterIds = new Dictionary<string, string>(StringComparer.Ordinal);

        var children = appointments
            .Where(appointment => SafeGet(() => (AppointmentType?)appointment.AppointmentType) is AppointmentType.Occurrence or AppointmentType.Exception)
            .ToList();
        if (children.Count == 0)
            return masterIds;

        var lookups = await service
            .BindToItems(children.Select(child => (ItemId)new RecurringAppointmentMasterId(child.Id.UniqueId)), new PropertySet(BasePropertySet.IdOnly), cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < children.Count; index++)
        {
            var response = lookups[index];
            if (response.Result == ServiceResult.Success && response.Item?.Id is { } masterId)
            {
                masterIds[children[index].Id.UniqueId] = masterId.UniqueId;
                continue;
            }

            // The occurrence stays visible as its own row; the series cannot be edited as a whole until its master is read.
            _logger.Warning("EWS calendar {Calendar}: master of occurrence '{Title}' not read ({Error}: {Message}).",
                calendar.Name, SafeGet(() => children[index].Subject), response.ErrorCode, response.ErrorMessage);
        }

        var newMasterIds = masterIds.Values.Distinct(StringComparer.Ordinal).Where(id => !storedMasters.ContainsKey(id)).ToList();
        if (newMasterIds.Count == 0)
            return masterIds;

        var masters = await service
            .BindToItems(newMasterIds.Select(id => new ItemId(id)), MasterPropertySet, cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < newMasterIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = masters[index];
            if (response.Result != ServiceResult.Success || response.Item is not Appointment master)
            {
                _logger.Warning("EWS calendar {Calendar}: series master not read ({Error}: {Message}); its occurrences are stored unlinked.",
                    calendar.Name, response.ErrorCode, response.ErrorMessage);
                storedMasters[newMasterIds[index]] = null;
                continue;
            }

            var synced = MapToSyncedMaster(master);
            storedMasters[newMasterIds[index]] = synced?.RemoteId;
            if (synced == null)
            {
                _logger.Warning("EWS calendar {Calendar}: the pattern of series '{Title}' has no RRULE form; its occurrences are stored unlinked.",
                    calendar.Name, SafeGet(() => master.Subject));
                continue;
            }

            seenRemoteIds.Add(master.Id.UniqueId);
            await ExchangeChangeProcessor.ManageCalendarEventAsync(synced, calendar, Account).ConfigureAwait(false);
        }

        return masterIds;
    }

    /// <summary>A series master as the stored, never rendered, row its occurrences link to; null when its pattern has no RRULE form.</summary>
    private static SyncedCalendarEvent? MapToSyncedMaster(Appointment master)
    {
        var recurrence = SafeGet(() => master.Recurrence);
        if (recurrence == null)
            return null;

        var synced = MapToSyncedEvent(master);
        var firstStartWallClock = synced.StartUtc;
        if (!string.IsNullOrEmpty(synced.TimeZoneIana) && TimeZoneInfo.TryFindSystemTimeZoneById(synced.TimeZoneIana, out var zone))
            firstStartWallClock = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(synced.StartUtc, DateTimeKind.Utc), zone);

        synced.Recurrence = EwsRecurrenceMapper.ToRecurrenceRule(recurrence, firstStartWallClock);
        return synced.Recurrence == null ? null : synced;
    }

    /// <summary>
    /// An EWS appointment in the neutral shape: a single appointment, an occurrence or exception from the calendar view,
    /// or a series master. Only singles and masters carry the client tracking id; occurrences inherit the master's
    /// property but are never the item a create made.
    /// </summary>
    private static SyncedCalendarEvent MapToSyncedEvent(Appointment appointment)
    {
        Guid? clientTrackingId = null;
        var appointmentType = SafeGet(() => (AppointmentType?)appointment.AppointmentType);
        if (appointmentType is not (AppointmentType.Occurrence or AppointmentType.Exception) &&
            appointment.TryGetProperty(ExchangeCalendarSchema.WinoClientTrackingId, out string trackingValue) &&
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
            ReminderMinutesBeforeStart = isReminderSet ? SafeGet(() => (int?)appointment.ReminderMinutesBeforeStart) : null,
            Attachments = ReadAttachments(appointment)
        };
    }

    /// <summary>
    /// The file attachments of an appointment (MS-OXWSATT); null when they were not loaded. Inline files belong to the
    /// body and item attachments have no file to download, so neither is listed. An occurrence lists the attachments
    /// of the series or, for an exception, its own; each downloads by the id given here.
    /// </summary>
    private static List<SyncedCalendarAttachment>? ReadAttachments(Item item)
    {
        var attachments = SafeGet(() => item.Attachments);
        if (attachments == null)
            return null;

        return attachments
            .OfType<FileAttachment>()
            .Where(attachment => !attachment.IsInline && !string.IsNullOrEmpty(attachment.Id))
            .Select(attachment => new SyncedCalendarAttachment
            {
                RemoteId = attachment.Id,
                FileName = string.IsNullOrWhiteSpace(attachment.Name) ? "attachment" : attachment.Name,
                ContentType = attachment.ContentType,
                Size = attachment.Size,
                LastModified = attachment.LastModifiedTime == default ? null : new DateTimeOffset(DateTime.SpecifyKind(attachment.LastModifiedTime, DateTimeKind.Utc))
            })
            .ToList();
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

                // A series request maps to the stored series master, as on MAPI; the calendar sync links its occurrences.
                var isMaster = SafeGet(() => (AppointmentType?)match.AppointmentType) == AppointmentType.RecurringMaster;
                var appointment = await Appointment.Bind(service, match.Id, isMaster ? MasterPropertySet : EventPropertySet).ConfigureAwait(false);
                var synced = isMaster ? MapToSyncedMaster(appointment) : MapToSyncedEvent(appointment);
                if (synced == null)
                    continue;

                await ExchangeChangeProcessor.ManageCalendarEventAsync(synced, calendar, Account).ConfigureAwait(false);

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
        var attachmentDrafts = request.ComposeResult?.Attachments;

        return Bundle(async (service, cancellationToken) =>
        {
            var appointment = new Appointment(service);
            ApplyCalendarItem(appointment, item, attendees, reminders);

            // CreateItem carries the files inline, so the invitation goes out with them (MS-OXWSCORE, MS-OXWSATT).
            var files = await ExchangeCalendarAttachments.ReadAsync(attachmentDrafts, cancellationToken).ConfigureAwait(false);
            foreach (var file in files)
                appointment.Attachments.AddFileAttachment(file.FileName, file.Content).ContentType = file.ContentType;

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

            // A series is stored as its master, which the sync adopts by the tracking id and links its occurrences to.
            await ExchangeChangeProcessor.PersistCreatedCalendarEventAsync(
                item,
                request.PreparedEvent.Attendees,
                request.PreparedEvent.Reminders,
                appointment.Id.UniqueId.WithClientTrackingId(item.Id)).ConfigureAwait(false);

            if (files.Count > 0)
                await StoreServerAttachmentsAsync(service, appointment.Id.UniqueId, item.Id).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var item = request.Item;
        var attendees = request.Attendees;
        var wantedAttachments = request.Attachments;

        return Bundle(async (service, cancellationToken) =>
        {
            var remoteId = item.RemoteEventId.GetProviderRemoteEventId();
            var attachmentPlan = ExchangeCalendarAttachments.Plan(await ExchangeChangeProcessor.GetCalendarAttachmentsAsync(item.Id).ConfigureAwait(false), wantedAttachments);

            // The managed API sends UpdateItem before it creates or deletes attachments, so the update the attendees get
            // would carry the old files. The files change first, in their own CreateAttachment and DeleteAttachment calls.
            if (attachmentPlan is { IsEmpty: false })
                await ApplyAttachmentPlanAsync(service, remoteId, attachmentPlan, cancellationToken).ConfigureAwait(false);

            // The row's id is the series master's for a whole-series edit and the occurrence's for one occurrence, so the
            // same UpdateItem changes the series or makes the occurrence an exception (MS-OXWSMTGS).
            var appointment = await Appointment.Bind(service, new ItemId(remoteId),
                new PropertySet(BasePropertySet.FirstClassProperties, AppointmentSchema.AppointmentType, AppointmentSchema.Recurrence)).ConfigureAwait(false);
            var isOrganizer = OrganizesAppointment(appointment);
            ApplyCalendarItem(appointment, item, attendees, request.Reminders, isOrganizer ? null : service.TimeZone);

            if (isOrganizer && SafeGet(() => (AppointmentType?)appointment.AppointmentType) == AppointmentType.RecurringMaster)
                ApplySeriesPattern(appointment, item);

            // Only the organizer re-sends the request; an attendee editing their copy just saves it.
            var sendMode = HasAttendees(appointment) && isOrganizer
                ? SendInvitationsOrCancellationsMode.SendToAllAndSaveCopy
                : SendInvitationsOrCancellationsMode.SendToNone;
            await appointment.Update(ConflictResolutionMode.AutoResolve, sendMode).ConfigureAwait(false);
            await ExchangeChangeProcessor.PersistUpdatedCalendarEventAsync(item, attendees, request.Reminders).ConfigureAwait(false);

            if (attachmentPlan is { IsEmpty: false })
                await StoreServerAttachmentsAsync(service, remoteId, item.Id).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        => UpdateCalendarEvent(request);

    public override List<IRequestBundle<EwsRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
    {
        var remoteId = request.Item.RemoteEventId;

        return Bundle(async (service, _) =>
        {
            var appointment = await Appointment.Bind(service, new ItemId(remoteId.GetProviderRemoteEventId()),
                new PropertySet(BasePropertySet.IdOnly, AppointmentSchema.IsMeeting, AppointmentSchema.MyResponseType)).ConfigureAwait(false);

            // An organizer deleting a meeting cancels it for the attendees, as Outlook does.
            var sendMode = OrganizesAppointment(appointment) && appointment.IsMeeting
                ? SendCancellationsMode.SendToAllAndSaveCopy
                : SendCancellationsMode.SendToNone;
            await appointment.Delete(DeleteMode.MoveToDeletedItems, sendMode).ConfigureAwait(false);
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

    /// <summary>
    /// Writes the series pattern only when the edit changed it: a new pattern makes Exchange drop the series' changed
    /// and deleted occurrences, so an edit of the title or time keeps them, as on MAPI.
    /// </summary>
    private void ApplySeriesPattern(Appointment master, CalendarItem item)
    {
        var rule = EwsRecurrenceMapper.GetRecurrenceRule(item);
        if (string.IsNullOrEmpty(rule))
        {
            master.Recurrence = null;
            return;
        }

        var requested = EwsRecurrenceMapper.Create(rule, item.StartDate);
        if (requested is null)
        {
            _logger.Warning("The recurrence rule of '{Title}' has no Exchange equivalent; the series keeps its pattern.", item.Title);
            return;
        }

        var current = SafeGet(() => master.Recurrence);
        if (current == null || !EwsRecurrenceMapper.IsSamePattern(current, requested, item.StartDate))
            master.Recurrence = requested;
    }

    /// <summary>
    /// Removes the planned attachments and adds the new files on the appointment (MS-OXWSATT DeleteAttachment and
    /// CreateAttachment). The item itself is not changed, so no UpdateItem and no meeting message is sent here. An
    /// attachment to remove that the server no longer has is already gone.
    /// </summary>
    private static async Task ApplyAttachmentPlanAsync(ExchangeService service, string remoteId, ExchangeAttachmentPlan plan, CancellationToken cancellationToken)
    {
        var files = await ExchangeCalendarAttachments.ReadAsync(plan.Added, cancellationToken).ConfigureAwait(false);
        var appointment = await Appointment.Bind(service, new ItemId(remoteId), new PropertySet(BasePropertySet.IdOnly, ItemSchema.Attachments)).ConfigureAwait(false);

        var removedIds = plan.Removed.Select(attachment => attachment.RemoteAttachmentId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        foreach (var attachment in appointment.Attachments.Where(attachment => removedIds.Contains(attachment.Id)).ToList())
            appointment.Attachments.Remove(attachment);

        foreach (var file in files)
            appointment.Attachments.AddFileAttachment(file.FileName, file.Content).ContentType = file.ContentType;

        await appointment.Update(ConflictResolutionMode.AutoResolve, SendInvitationsOrCancellationsMode.SendToNone).ConfigureAwait(false);
    }

    /// <summary>Stores the attachments the server now holds for the event, once a write that changed them succeeded.</summary>
    private async Task StoreServerAttachmentsAsync(ExchangeService service, string remoteId, Guid calendarItemId)
    {
        var appointment = await Appointment.Bind(service, new ItemId(remoteId), new PropertySet(BasePropertySet.IdOnly, ItemSchema.Attachments)).ConfigureAwait(false);
        await ExchangeChangeProcessor.SaveCalendarAttachmentsAsync(calendarItemId, ReadAttachments(appointment) ?? []).ConfigureAwait(false);
    }

    /// <summary>Downloads one file attachment of an event with GetAttachment (MS-OXWSATT).</summary>
    public override async Task DownloadCalendarAttachmentAsync(CalendarItem calendarItem, CalendarAttachment attachment, string localFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (string.IsNullOrEmpty(attachment.RemoteAttachmentId))
            throw new InvalidOperationException("The attachment has no server id.");

        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var responses = await service.GetAttachments([attachment.RemoteAttachmentId], null, null, cancellationToken).ConfigureAwait(false);
        var response = responses[0];
        if (response.Result != ServiceResult.Success)
            throw new InvalidOperationException($"EWS GetAttachment failed: {response.ErrorCode} {response.ErrorMessage}");
        if (response.Attachment is not FileAttachment { Content: { } content })
            throw new InvalidOperationException("The EWS attachment has no file content.");

        await System.IO.File.WriteAllBytesAsync(localFilePath, content, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasAttendees(Appointment appointment)
        => appointment.RequiredAttendees.Count > 0 || appointment.OptionalAttendees.Count > 0;

    // A new appointment has no response type yet and is the user's own.
    private static bool OrganizesAppointment(Appointment appointment)
        => appointment.IsNew || appointment.MyResponseType is MeetingResponseType.Organizer or MeetingResponseType.Unknown;

    /// <param name="attendeeCopyServiceZone">
    /// Set when the appointment is an attendee's copy of someone else's meeting. Exchange refuses changes to its time
    /// zones and attendee list there ("Set action is invalid for property"), so the times are written in the service's
    /// zone and the attendees are left alone.
    /// </param>
    private static void ApplyCalendarItem(Appointment appointment, CalendarItem item, List<CalendarEventAttendee>? attendees, List<Reminder>? reminders,
                                          TimeZoneInfo? attendeeCopyServiceZone = null)
    {
        appointment.Subject = item.Title;
        appointment.Body = new MessageBody(BodyType.HTML, item.Description ?? string.Empty);
        appointment.Location = item.Location;

        // The start is wall clock in the item's zone; the appointment carries that zone so EWS reads it correctly
        // whatever the service zone is.
        var timeZone = ResolveTimeZoneInfo(item.StartTimeZone);
        if (attendeeCopyServiceZone is null)
        {
            appointment.StartTimeZone = timeZone;
            appointment.EndTimeZone = timeZone;
            appointment.Start = item.StartDate;
            appointment.End = item.StartDate.AddSeconds(item.DurationInSeconds);
        }
        else
        {
            var start = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(item.StartDate, DateTimeKind.Unspecified), timeZone, attendeeCopyServiceZone);
            appointment.Start = start;
            appointment.End = start.AddSeconds(item.DurationInSeconds);
            attendees = null;
        }
        appointment.IsAllDayEvent = item.IsAllDayEvent;
        appointment.Sensitivity = item.Visibility switch
        {
            CalendarItemVisibility.Private => Sensitivity.Private,
            CalendarItemVisibility.Confidential => Sensitivity.Confidential,
            _ => Sensitivity.Normal
        };

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

        // Exchange keeps one reminder before the start; the others stay in Wino and fire from there.
        if (reminders != null)
        {
            var serverReminder = ReminderSyncPlan.Split(reminders, CalendarCapabilities.Exchange).Server.FirstOrDefault();
            appointment.IsReminderSet = serverReminder != null;
            if (serverReminder != null)
                appointment.ReminderMinutesBeforeStart = (int)(serverReminder.DurationInSeconds / 60);
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

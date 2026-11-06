using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.MAPI;
using Wino.Core.MAPI.Calendar;
using Wino.Core.MAPI.Rops;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Synchronizers.Exchange;

namespace Wino.Core.Synchronizers.Mapi;

// Calendars are the IPF.Appointment folders of the mailbox, keyed "mapi:" plus the folder id. The server does not expand
// a contents table the way an EWS calendar view does, so series are expanded here: the master is stored (never rendered)
// and its occurrences are stored flat as "mapi:{master}:{original start}", so an exception keeps the id of its slot.
public sealed partial class MapiExchangeSynchronizer
{
    private static LogonResponse RequireLogon(MapiSession session)
        => session.Logon ?? throw new InvalidOperationException("The MAPI session has no mailbox logon.");

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
            return CalendarSynchronizationResult.Empty;

        if (!IsProviderCalendarAvailable)
            return CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable));

        try
        {
            await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            var session = lease.Session;
            await SynchronizeMapiCalendarsAsync(session, cancellationToken).ConfigureAwait(false);

            var tags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
            var windowStartUtc = DateTime.UtcNow.AddMonths(-CalendarWindowPastMonths);
            var windowEndUtc = DateTime.UtcNow.AddMonths(CalendarWindowFutureMonths);

            foreach (var calendar in await GetCalendarsToSynchronizeAsync(options).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseMapiId(calendar.RemoteCalendarId, out var folderId))
                    continue;

                var rows = await MapiCalendarOperations.ReadAppointmentsAsync(session, folderId, tags, cancellationToken, Diagnostics).ConfigureAwait(false);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var occurrences = 0;
                foreach (var row in rows.Where(r => r.IsAppointment))
                {
                    await ReadAttendeesIfMeetingAsync(session, folderId, row, cancellationToken).ConfigureAwait(false);

                    // The master goes first so its occurrences can link to it.
                    if (MapiCalendarExpander.Master(row) is { } master)
                    {
                        seen.Add(master.RemoteId.GetProviderRemoteEventId());
                        await ExchangeChangeProcessor.ManageCalendarEventAsync(master, calendar, Account).ConfigureAwait(false);
                    }

                    foreach (var occurrence in MapiCalendarExpander.Expand(row, windowStartUtc, windowEndUtc, Diagnostics))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        seen.Add(occurrence.RemoteId.GetProviderRemoteEventId());
                        await ExchangeChangeProcessor.ManageCalendarEventAsync(occurrence, calendar, Account).ConfigureAwait(false);
                        occurrences++;
                    }
                }

                var removed = 0;
                var localEvents = await ExchangeChangeProcessor.GetCalendarItemsInRangeAsync(calendar, windowStartUtc, windowEndUtc).ConfigureAwait(false);
                foreach (var local in localEvents)
                {
                    if (!string.IsNullOrEmpty(local.RemoteEventId) && !seen.Contains(local.RemoteEventId.GetProviderRemoteEventId()))
                    {
                        await ExchangeChangeProcessor.DeleteCalendarItemAsync(local.Id).ConfigureAwait(false);
                        removed++;
                    }
                }

                // Masters are outside the range query; a series gone from the server takes its occurrences with it.
                foreach (var master in await ExchangeChangeProcessor.GetRecurringMastersAsync(calendar).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(master.RemoteEventId) && !seen.Contains(master.RemoteEventId.GetProviderRemoteEventId()))
                    {
                        await ExchangeChangeProcessor.DeleteCalendarItemAsync(master.Id).ConfigureAwait(false);
                        removed++;
                    }
                }

                Logger.Information("MAPI calendar {Account}/{Calendar}: {Rows} rows, {Occurrences} occurrences in window, {Removed} removed.", Account.Address, calendar.Name, rows.Count, occurrences, removed);
            }

            return CalendarSynchronizationResult.Empty;
        }
        catch (OperationCanceledException)
        {
            return CalendarSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MAPI calendar synchronization failed for {Account}.", Account.Address);
            return CalendarSynchronizationResult.Failed(ex);
        }
    }

    private async Task ReadAttendeesIfMeetingAsync(MapiSession session, ulong folderId, MapiAppointmentInfo row, CancellationToken cancellationToken)
    {
        if (!row.IsMeeting)
            return;

        try
        {
            row.Attendees = await MapiCalendarOperations.ReadAttendeesAsync(session, folderId, row.MessageId, cancellationToken, Diagnostics).ConfigureAwait(false);
        }
        catch (MapiException ex)
        {
            Logger.Debug(ex, "MAPI calendar {Account}: attendees of 0x{Id:X16} not read.", Account.Address, row.MessageId);
        }
    }

    /// <summary>
    /// Reconciles the calendar folders. Rows that still carry an EWS id from before the transport changed are re-keyed in
    /// place (the primary to the default calendar, others by name) so colours and sync preferences survive the switch.
    /// A calendar without a remote id is a local calendar the account kept; it is left alone.
    /// </summary>
    private async Task SynchronizeMapiCalendarsAsync(MapiSession session, CancellationToken cancellationToken)
    {
        var logon = RequireLogon(session);
        var defaultFolderId = await MapiCalendarOperations.FindCalendarFolderIdAsync(session, logon.InboxFolderId, cancellationToken).ConfigureAwait(false);
        var hierarchy = await MapiFolderOperations.ReadHierarchyAsync(session, logon.IpmSubtreeFolderId, cancellationToken, Diagnostics).ConfigureAwait(false);

        var remote = new Dictionary<ulong, string>();
        foreach (var folder in hierarchy.Where(f => string.Equals(f.ContainerClass, PropertyTags.CalendarContainerClass, StringComparison.OrdinalIgnoreCase)))
            remote[folder.FolderId] = string.IsNullOrWhiteSpace(folder.DisplayName) ? Translator.AccountDetailsPage_TabCalendar : folder.DisplayName;

        if (defaultFolderId is { } primary && !remote.ContainsKey(primary))
            remote[primary] = Translator.AccountDetailsPage_TabCalendar;

        var local = (await ExchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false))
            .Where(c => !string.IsNullOrWhiteSpace(c.RemoteCalendarId))
            .ToList();

        foreach (var calendar in local.Where(c => !TryParseMapiId(c.RemoteCalendarId, out _)).ToList())
        {
            ulong? target = null;
            if (calendar.IsPrimary && defaultFolderId is { } primaryId)
            {
                target = primaryId;
            }
            else
            {
                // Otherwise by name, skipping folders another row has already claimed.
                var byName = remote.FirstOrDefault(r => string.Equals(r.Value, calendar.Name, StringComparison.OrdinalIgnoreCase)
                                                        && !local.Any(l => l.RemoteCalendarId == ToMapiId(r.Key))).Key;
                if (byName != 0)
                    target = byName;
            }

            if (target is { } folderId && !local.Any(l => l.RemoteCalendarId == ToMapiId(folderId)))
            {
                Logger.Information("MAPI calendar {Account}: re-keying '{Name}' from its EWS id to 0x{Folder:X16}.", Account.Address, calendar.Name, folderId);
                calendar.RemoteCalendarId = ToMapiId(folderId);
                await ExchangeChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
            }
        }

        foreach (var calendar in local)
        {
            if (!TryParseMapiId(calendar.RemoteCalendarId, out var folderId) || !remote.ContainsKey(folderId))
                await ExchangeChangeProcessor.DeleteAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        foreach (var (folderId, name) in remote)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteId = ToMapiId(folderId);
            var isPrimary = folderId == defaultFolderId;
            var existing = local.FirstOrDefault(c => c.RemoteCalendarId == remoteId);
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

    /// <summary>
    /// Links a meeting request to the calendar item it created: the appointment whose clean global object id forms the
    /// request's UID is looked up in each synced calendar, stored when the calendar sync has not reached it yet, and the
    /// mapping row is written. Failures are logged; the mail itself is saved regardless.
    /// </summary>
    private async Task TryMapCalendarInvitationAsync(MapiSession session, MapiCalendarTags tags, MailCopy mailCopy, string? invitationUid, CancellationToken cancellationToken)
    {
        if (!IsProviderCalendarAvailable || string.IsNullOrWhiteSpace(invitationUid))
            return;

        var calendars = await GetCalendarsToSynchronizeAsync(null).ConfigureAwait(false);

        foreach (var calendar in calendars.OrderByDescending(c => c.IsPrimary))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseMapiId(calendar.RemoteCalendarId, out var calendarFolderId))
                continue;

            try
            {
                var rows = await MapiCalendarOperations.ReadAppointmentsAsync(session, calendarFolderId, tags, cancellationToken, Diagnostics).ConfigureAwait(false);
                var row = rows.FirstOrDefault(r => r.IsAppointment
                                                   && r.CleanGlobalObjectId is { Length: > 0 } goid
                                                   && string.Equals(Convert.ToHexString(goid), invitationUid, StringComparison.OrdinalIgnoreCase));
                if (row == null)
                    continue;

                var remoteEventId = ToMapiId(row.MessageId);
                var localCalendarItem = await ExchangeChangeProcessor.GetCalendarItemAsync(calendar.Id, remoteEventId).ConfigureAwait(false);

                if (localCalendarItem == null)
                {
                    await ReadAttendeesIfMeetingAsync(session, calendarFolderId, row, cancellationToken).ConfigureAwait(false);

                    if (MapiCalendarExpander.Master(row) is { } master)
                        await ExchangeChangeProcessor.ManageCalendarEventAsync(master, calendar, Account).ConfigureAwait(false);

                    var windowStartUtc = DateTime.UtcNow.AddMonths(-CalendarWindowPastMonths);
                    var windowEndUtc = DateTime.UtcNow.AddMonths(CalendarWindowFutureMonths);
                    foreach (var occurrence in MapiCalendarExpander.Expand(row, windowStartUtc, windowEndUtc, Diagnostics))
                        await ExchangeChangeProcessor.ManageCalendarEventAsync(occurrence, calendar, Account).ConfigureAwait(false);

                    localCalendarItem = await ExchangeChangeProcessor.GetCalendarItemAsync(calendar.Id, remoteEventId).ConfigureAwait(false);
                }

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
                    CalendarRemoteEventId = remoteEventId
                }).ConfigureAwait(false);

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "MAPI {Account}: invitation mail {MailCopyId} not mapped to calendar {CalendarId}.", Account.Address, mailCopy.Id, calendar.Id);
            }
        }
    }

    #region Calendar writes

    /// <summary>
    /// An occurrence row's id is "mapi:{master}:{original start}"; a single appointment's is "mapi:{id}". Returns the
    /// master id and the original start when the item is an occurrence.
    /// </summary>
    private static bool TryParseOccurrenceId(IAccountCalendar? calendar, CalendarItem item, out ulong folderId, out ulong masterId, out DateTime originalStartUtc)
    {
        masterId = 0;
        originalStartUtc = default;
        if (!TryParseMapiId(calendar?.RemoteCalendarId, out folderId))
            return false;

        var remoteId = item.RemoteEventId?.GetProviderRemoteEventId() ?? string.Empty;
        var parts = remoteId.Split(':');
        if (parts.Length != 3 || !TryParseMapiId(parts[0] + ":" + parts[1], out masterId))
            return false;

        return DateTime.TryParseExact(parts[2], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out originalStartUtc);
    }

    private static (ulong FolderId, ulong MessageId) RequireAppointmentIds(IAccountCalendar? calendar, CalendarItem item)
    {
        if (!TryParseMapiId(calendar?.RemoteCalendarId, out var folderId))
            throw new InvalidOperationException("The calendar is not addressable over MAPI until the next calendar sync.");

        var remoteId = item.RemoteEventId?.GetProviderRemoteEventId() ?? string.Empty;
        if (!TryParseMapiId(remoteId, out var messageId))
            throw new InvalidOperationException("The event is not addressable over MAPI until the next calendar sync.");

        return (folderId, messageId);
    }

    private MapiCalendarOperations.AppointmentWrite ToAppointmentWrite(CalendarItem item, List<Reminder>? reminders, string? clientTrackingId, List<CalendarEventAttendee>? attendees = null)
    {
        var startUtc = DateTime.SpecifyKind(item.StartDate, DateTimeKind.Utc);
        if (!string.IsNullOrEmpty(item.StartTimeZone) && TimeZoneInfo.TryFindSystemTimeZoneById(item.StartTimeZone, out var zone))
            startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(item.StartDate, DateTimeKind.Unspecified), zone);

        var write = new MapiCalendarOperations.AppointmentWrite
        {
            OrganizerName = Account.SenderName ?? Account.Name ?? Account.Address,
            OrganizerAddress = Account.Address,
            TimeZoneId = item.StartTimeZone,
            RecurrenceRule = EwsRecurrenceMapper.GetRecurrenceRule(item),
            Subject = item.Title,
            Body = item.Description,
            Location = item.Location,
            StartUtc = startUtc,
            EndUtc = startUtc.AddSeconds(item.DurationInSeconds),
            AllDay = item.IsAllDayEvent,
            BusyStatus = item.ShowAs switch
            {
                CalendarItemShowAs.Free => 0u,
                CalendarItemShowAs.Tentative => 1u,
                CalendarItemShowAs.OutOfOffice => 3u,
                CalendarItemShowAs.WorkingElsewhere => 4u,
                _ => 2u
            },
            Sensitivity = item.Visibility switch
            {
                CalendarItemVisibility.Private => 2u,
                CalendarItemVisibility.Confidential => 3u,
                _ => 0u
            },
            ReminderMinutes = reminders?.FirstOrDefault() is { } reminder ? (int)(reminder.DurationInSeconds / 60) : null,
            ClientTrackingId = clientTrackingId
        };

        foreach (var attendee in attendees ?? [])
        {
            if (attendee.IsOrganizer || string.IsNullOrWhiteSpace(attendee.Email))
                continue;

            if (string.Equals(attendee.Email, Account.Address, StringComparison.OrdinalIgnoreCase))
                continue;

            write.Attendees.Add(new MapiCalendarOperations.MeetingAttendee(attendee.Name, attendee.Email.Trim(), attendee.IsOptionalAttendee));
        }

        return write;
    }

    /// <summary>
    /// After one occurrence of a meeting the account organizes was changed or removed, the attendees get the matching
    /// instance-level request or cancellation (MS-OXOCAL 3.1.4.4.3 and 3.1.4.6). Best effort: the change stands if the
    /// send fails.
    /// </summary>
    private async Task NotifyOccurrenceChangeAsync(MapiSession session, ulong folderId, ulong masterId, MapiCalendarTags tags, DateTime originalStartUtc,
        MapiCalendarOperations.AppointmentWrite change, CalendarItem item, bool cancel, CancellationToken cancellationToken)
    {
        if (!OrganizesItem(item))
            return;

        try
        {
            var master = await MapiCalendarOperations.ReadMeetingIdentityAsync(session, folderId, masterId, tags, cancellationToken, Diagnostics).ConfigureAwait(false);
            var attendees = (master.Attendees ?? [])
                .Where(r => !r.IsOrganizer && !string.IsNullOrEmpty(r.SmtpAddress) && !string.Equals(r.SmtpAddress, Account.Address, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (attendees.Count == 0 || !GlobalObjectId.IsWellFormed(master.GlobalObjectId))
                return;

            var zone = TimeZoneDefinition.Resolve(change.TimeZoneId);
            var originalWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(originalStartUtc, DateTimeKind.Utc), zone);
            var now = DateTime.UtcNow;
            var message = cancel
                ? MapiCalendarOperations.BuildOccurrenceCancellation(tags, master, originalStartUtc, originalWall, master.Sequence, now)
                : MapiCalendarOperations.BuildOccurrenceRequest(tags, master, change, originalStartUtc, originalWall, master.Sequence, now);
            var logon = RequireLogon(session);
            await MapiMessageComposer.SendAsync(session, logon.OutboxFolderId, logon.SentItemsFolderId, message, cancellationToken, Diagnostics, interactive: true).ConfigureAwait(false);
            Logger.Information("MAPI calendar {Account}: {Kind} for occurrence {Start:u} of 0x{Id:X16} sent to {Attendees} attendees.", Account.Address, cancel ? "cancellation" : "update", originalStartUtc, masterId, attendees.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI calendar {Account}: attendees were not told about the occurrence change of 0x{Id:X16}.", Account.Address, masterId);
        }
    }

    /// <summary>The account organizes the item when it names no organizer or names this account.</summary>
    private bool OrganizesItem(CalendarItem item)
        => string.IsNullOrEmpty(item.OrganizerEmail) || string.Equals(item.OrganizerEmail, Account.Address, StringComparison.OrdinalIgnoreCase);

    public override List<IRequestBundle<EwsRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var item = request.PreparedItem;
        var reminders = request.ComposeResult?.SelectedReminders;
        var calendar = request.AssignedCalendar;
        var attendees = request.ComposeResult?.Attendees is { Count: > 0 } composed ? composed : null;

        return MapiBundle(async (session, cancellationToken) =>
        {
            if (!TryParseMapiId(calendar.RemoteCalendarId, out var folderId))
                throw new InvalidOperationException("The calendar is not addressable over MAPI until the next calendar sync.");

            var tags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
            var write = ToAppointmentWrite(item, reminders, item.Id.ToString("N"), attendees);
            var logon = RequireLogon(session);
            var id = write.IsMeeting
                ? await MapiCalendarOperations.CreateMeetingAsync(session, folderId, logon.OutboxFolderId, logon.SentItemsFolderId, tags, write, cancellationToken, Diagnostics).ConfigureAwait(false)
                : await MapiCalendarOperations.CreateAppointmentAsync(session, folderId, tags, write, cancellationToken).ConfigureAwait(false);
            Logger.Information("MAPI calendar {Account}: created 0x{Id:X16} ({Kind}, {Attendees} attendees, recurring={Recurring}).", Account.Address, id, write.IsMeeting ? "meeting" : "appointment", write.Attendees.Count, write.IsRecurring);

            // The new id goes on the local row at once so the optimistic item is adopted rather than duplicated by the
            // resync; a series master reconciles by the same id.
            await ExchangeChangeProcessor.PersistCreatedCalendarEventAsync(
                item,
                request.PreparedEvent.Attendees,
                request.PreparedEvent.Reminders,
                ToMapiId(id).WithClientTrackingId(item.Id)).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var item = request.Item;
        var attendees = request.Attendees is { Count: > 0 } requested ? requested : null;

        return MapiBundle(async (session, cancellationToken) =>
        {
            if (TryParseOccurrenceId(item.AssignedCalendar, item, out var seriesFolderId, out var masterId, out var originalStartUtc))
            {
                var seriesTags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
                var change = ToAppointmentWrite(item, null, null);
                await MapiOccurrenceOperations.ModifyOccurrenceAsync(session, seriesFolderId, masterId, seriesTags, originalStartUtc, change, cancellationToken, Diagnostics).ConfigureAwait(false);
                await NotifyOccurrenceChangeAsync(session, seriesFolderId, masterId, seriesTags, originalStartUtc, change, item, cancel: false, cancellationToken).ConfigureAwait(false);
                return;
            }

            var (folderId, messageId) = RequireAppointmentIds(item.AssignedCalendar, item);
            var tags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
            var write = ToAppointmentWrite(item, null, null, attendees);

            // Editing a series rewrites its pattern; its deleted and changed occurrences survive when the pattern itself
            // did not change, as Outlook keeps them.
            if (write.IsRecurring)
            {
                try
                {
                    write.ExistingRecurrence = (await MapiOccurrenceOperations.ReadMasterAsync(session, folderId, messageId, tags, cancellationToken).ConfigureAwait(false)).Recurrence;
                }
                catch (MapiException ex)
                {
                    Logger.Debug(ex, "MAPI calendar {Account}: existing pattern of 0x{Id:X16} not read; its exceptions are not carried over.", Account.Address, messageId);
                }
            }

            // Only the organizer re-sends the request; an attendee editing their copy just saves it.
            if (write.IsMeeting && OrganizesItem(item))
            {
                var logon = RequireLogon(session);
                await MapiCalendarOperations.UpdateMeetingAsync(session, folderId, messageId, logon.OutboxFolderId, logon.SentItemsFolderId, tags, write, cancellationToken, Diagnostics).ConfigureAwait(false);
                Logger.Information("MAPI calendar {Account}: meeting 0x{Id:X16} updated and sent to {Attendees} attendees.", Account.Address, messageId, write.Attendees.Count);
            }
            else
            {
                await MapiCalendarOperations.UpdateAppointmentAsync(session, folderId, messageId, tags, write, cancellationToken).ConfigureAwait(false);
            }
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        => UpdateCalendarEvent(request);

    public override List<IRequestBundle<EwsRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
    {
        var item = request.Item;

        return MapiBundle(async (session, cancellationToken) =>
        {
            if (TryParseOccurrenceId(item.AssignedCalendar, item, out var seriesFolderId, out var masterId, out var originalStartUtc))
            {
                var seriesTags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
                await MapiOccurrenceOperations.DeleteOccurrenceAsync(session, seriesFolderId, masterId, seriesTags, originalStartUtc, cancellationToken, Diagnostics).ConfigureAwait(false);
                Logger.Information("MAPI calendar {Account}: occurrence {Start:u} of 0x{Id:X16} deleted.", Account.Address, originalStartUtc, masterId);
                await NotifyOccurrenceChangeAsync(session, seriesFolderId, masterId, seriesTags, originalStartUtc, ToAppointmentWrite(item, null, null), item, cancel: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            var (folderId, messageId) = RequireAppointmentIds(item.AssignedCalendar, item);
            var logon = RequireLogon(session);

            // A meeting the account organizes is cancelled, so its attendees are told, rather than silently removed.
            if (OrganizesItem(item))
            {
                var tags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
                var cancelled = await MapiCalendarOperations.CancelMeetingAsync(session, folderId, messageId, logon.OutboxFolderId, logon.SentItemsFolderId, logon.DeletedItemsFolderId,
                    tags, Account.Address, cancellationToken, Diagnostics).ConfigureAwait(false);
                if (cancelled)
                    Logger.Information("MAPI calendar {Account}: meeting 0x{Id:X16} cancelled.", Account.Address, messageId);
                return;
            }

            await MapiMessageOperations.MoveAsync(session, folderId, logon.DeletedItemsFolderId, [messageId], cancellationToken: cancellationToken).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> AcceptEvent(AcceptEventRequest request)
        => RespondToMeeting(request, MapiCalendarOperations.MeetingResponse.Accept, request.Item, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> TentativeEvent(TentativeEventRequest request)
        => RespondToMeeting(request, MapiCalendarOperations.MeetingResponse.Tentative, request.Item, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> DeclineEvent(DeclineEventRequest request)
        => RespondToMeeting(request, MapiCalendarOperations.MeetingResponse.Decline, request.Item, request.ResponseMessage);

    // A response to one occurrence of a series is sent for the series: the master's id is the part before the
    // occurrence suffix.
    private List<IRequestBundle<EwsRequest>> RespondToMeeting(CalendarRequestBase request, MapiCalendarOperations.MeetingResponse response, CalendarItem item, string? comment)
    {
        return MapiBundle(async (session, cancellationToken) =>
        {
            if (!TryParseMapiId(item.AssignedCalendar?.RemoteCalendarId, out var folderId))
                throw new InvalidOperationException("The calendar is not addressable over MAPI until the next calendar sync.");

            var remoteId = item.RemoteEventId?.GetProviderRemoteEventId() ?? string.Empty;
            var secondColon = remoteId.IndexOf(':', remoteId.IndexOf(':') + 1);
            var masterRemoteId = secondColon > 0 ? remoteId[..secondColon] : remoteId;
            if (!TryParseMapiId(masterRemoteId, out var messageId))
                throw new InvalidOperationException("The event is not addressable over MAPI until the next calendar sync.");

            var tags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
            var logon = RequireLogon(session);
            await MapiCalendarOperations.RespondToMeetingAsync(session, folderId, messageId, logon.OutboxFolderId, logon.SentItemsFolderId, logon.DeletedItemsFolderId,
                tags, response, comment, Account.SenderName ?? Account.Name ?? Account.Address, cancellationToken, Diagnostics).ConfigureAwait(false);
            Logger.Information("MAPI calendar {Account}: {Response} sent for 0x{Id:X16}.", Account.Address, response, messageId);
        }, request, request);
    }

    #endregion
}

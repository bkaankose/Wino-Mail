using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Integration.Processors;

/// <summary>
/// EWS-specific change processor. Adds calendar event mapping (EWS <see cref="Appointment"/> ->
/// <see cref="CalendarItem"/>) on top of the default mail/folder/calendar-folder processing.
/// </summary>
public interface IExchangeChangeProcessor : IDefaultChangeProcessor
{
    /// <summary>
    /// Maps an EWS appointment to a <see cref="CalendarItem"/> and persists it (creating or updating).
    /// </summary>
    Task ManageCalendarEventAsync(Appointment appointment, AccountCalendar assignedCalendar, MailAccount organizerAccount);

    /// <summary>
    /// Returns the locally stored events for a calendar overlapping the given UTC window (for reconcile).
    /// </summary>
    Task<List<CalendarItem>> GetCalendarItemsInRangeAsync(AccountCalendar calendar, DateTime startUtc, DateTime endUtc);
}

public class ExchangeChangeProcessor : DefaultChangeProcessor, IExchangeChangeProcessor
{
    public ExchangeChangeProcessor(IDatabaseService databaseService,
                                   IFolderService folderService,
                                   IMailService mailService,
                                   ICalendarService calendarService,
                                   IAccountService accountService,
                                   IMimeFileService mimeFileService)
        : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService)
    {
    }

    public Task<List<CalendarItem>> GetCalendarItemsInRangeAsync(AccountCalendar calendar, DateTime startUtc, DateTime endUtc)
        => CalendarService.GetCalendarEventsAsync(calendar, new TimeRange(startUtc, endUtc));

    public async Task ManageCalendarEventAsync(Appointment appointment, AccountCalendar assignedCalendar, MailAccount organizerAccount)
    {
        var remoteEventId = appointment.Id.UniqueId;

        var savingItem = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, remoteEventId).ConfigureAwait(false);
        var isNewItem = savingItem == null;
        var savingItemId = isNewItem ? Guid.NewGuid() : savingItem.Id;
        savingItem ??= new CalendarItem { Id = savingItemId };

        // Appointment Start/End arrive in UTC (the calendar sync sets service.TimeZone = UTC). Store the
        // wall-clock time in the appointment's own zone when it resolves to IANA; otherwise store UTC.
        // Duration is an absolute span, so it is timezone-independent.
        var startUtc = DateTime.SpecifyKind(appointment.Start, DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(appointment.End, DateTimeKind.Utc);
        var (startDate, ianaTimeZone) = ResolveStart(appointment, startUtc);

        savingItem.RemoteEventId = remoteEventId;
        savingItem.StartDate = startDate;
        savingItem.DurationInSeconds = (endUtc - startUtc).TotalSeconds;
        savingItem.StartTimeZone = ianaTimeZone;
        savingItem.EndTimeZone = ianaTimeZone;

        savingItem.Title = appointment.Subject;
        savingItem.Description = SafeGetBody(appointment);
        savingItem.Location = appointment.Location;
        savingItem.CalendarId = assignedCalendar.Id;
        savingItem.AssignedCalendar = assignedCalendar;
        savingItem.OrganizerEmail = appointment.Organizer?.Address;
        savingItem.OrganizerDisplayName = appointment.Organizer?.Name;
        savingItem.CreatedAt = appointment.DateTimeCreated;
        savingItem.UpdatedAt = appointment.LastModifiedTime;
        savingItem.IsHidden = false;

        // CalendarView returns expanded occurrences, so each event is stored flat (no recurrence master).
        savingItem.Recurrence = null;
        savingItem.RecurringCalendarItemId = null;

        savingItem.Visibility = appointment.Sensitivity switch
        {
            Sensitivity.Personal => CalendarItemVisibility.Private,
            Sensitivity.Private => CalendarItemVisibility.Private,
            Sensitivity.Confidential => CalendarItemVisibility.Confidential,
            _ => CalendarItemVisibility.Public
        };

        savingItem.ShowAs = appointment.LegacyFreeBusyStatus switch
        {
            LegacyFreeBusyStatus.Free => CalendarItemShowAs.Free,
            LegacyFreeBusyStatus.Tentative => CalendarItemShowAs.Tentative,
            LegacyFreeBusyStatus.OOF => CalendarItemShowAs.OutOfOffice,
            LegacyFreeBusyStatus.WorkingElsewhere => CalendarItemShowAs.WorkingElsewhere,
            _ => CalendarItemShowAs.Busy
        };

        var isOrganizer = !string.IsNullOrEmpty(appointment.Organizer?.Address) &&
                          string.Equals(appointment.Organizer.Address, organizerAccount.Address, StringComparison.OrdinalIgnoreCase);
        savingItem.IsLocked = !isOrganizer;

        savingItem.Status = appointment.MyResponseType switch
        {
            MeetingResponseType.Tentative => CalendarItemStatus.Tentative,
            MeetingResponseType.Accept => CalendarItemStatus.Accepted,
            MeetingResponseType.Organizer => CalendarItemStatus.Accepted,
            MeetingResponseType.Decline => CalendarItemStatus.Cancelled,
            MeetingResponseType.NoResponseReceived => CalendarItemStatus.NotResponded,
            _ => CalendarItemStatus.Accepted
        };

        if (savingItem.Status == CalendarItemStatus.Cancelled)
            savingItem.IsHidden = true;

        var attendees = BuildAttendees(appointment, savingItemId);
        var reminders = BuildReminders(appointment, savingItemId);

        if (isNewItem)
            await CalendarService.CreateNewCalendarItemAsync(savingItem, attendees).ConfigureAwait(false);
        else
            await CalendarService.UpdateCalendarItemAsync(savingItem, attendees).ConfigureAwait(false);

        await CalendarService.SaveRemindersAsync(savingItemId, reminders).ConfigureAwait(false);
    }

    private static (DateTime startDate, string ianaTimeZone) ResolveStart(Appointment appointment, DateTime startUtc)
    {
        try
        {
            var timeZone = appointment.StartTimeZone;
            if (timeZone != null && TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var iana))
                return (TimeZoneInfo.ConvertTimeFromUtc(startUtc, timeZone), iana);
        }
        catch
        {
            // StartTimeZone wasn't available; fall back to the UTC instant.
        }

        return (startUtc, null);
    }

    private static string SafeGetBody(Appointment appointment)
    {
        try
        {
            return appointment.Body?.Text;
        }
        catch
        {
            return null;
        }
    }

    private static List<CalendarEventAttendee> BuildAttendees(Appointment appointment, Guid calendarItemId)
    {
        var attendees = new List<CalendarEventAttendee>();
        AddAttendees(attendees, appointment.RequiredAttendees, calendarItemId, isOptional: false);
        AddAttendees(attendees, appointment.OptionalAttendees, calendarItemId, isOptional: true);
        return attendees.Count == 0 ? null : attendees;
    }

    private static void AddAttendees(List<CalendarEventAttendee> target, AttendeeCollection source, Guid calendarItemId, bool isOptional)
    {
        if (source == null)
            return;

        foreach (var attendee in source)
        {
            if (string.IsNullOrEmpty(attendee.Address))
                continue;

            target.Add(new CalendarEventAttendee
            {
                Id = Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                Name = attendee.Name,
                Email = attendee.Address,
                IsOptionalAttendee = isOptional,
                AttendenceStatus = attendee.ResponseType switch
                {
                    MeetingResponseType.Accept => AttendeeStatus.Accepted,
                    MeetingResponseType.Tentative => AttendeeStatus.Tentative,
                    MeetingResponseType.Decline => AttendeeStatus.Declined,
                    _ => AttendeeStatus.NeedsAction
                }
            });
        }
    }

    private static List<Reminder> BuildReminders(Appointment appointment, Guid calendarItemId)
    {
        if (!appointment.IsReminderSet)
            return null;

        return new List<Reminder>
        {
            new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                DurationInSeconds = appointment.ReminderMinutesBeforeStart * 60L,
                ReminderType = CalendarItemReminderType.Popup
            }
        };
    }
}

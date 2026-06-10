using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Itenso.TimePeriod;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers.Exchange;
using Wino.Services;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Integration.Processors;

/// <summary>EWS-specific calendar event mapping on top of the default change processor.</summary>
public interface IExchangeChangeProcessor : IDefaultChangeProcessor
{
    Task ManageCalendarEventAsync(Appointment appointment, AccountCalendar assignedCalendar, MailAccount organizerAccount);

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
        Guid? clientTrackingId = null;
        if (appointment.TryGetProperty(ExchangeCalendarSchema.WinoClientTrackingId, out string trackingValue) &&
            Guid.TryParseExact(trackingValue, "N", out var parsedTrackingId))
        {
            clientTrackingId = parsedTrackingId;
        }

        var remoteEventId = appointment.Id.UniqueId.WithClientTrackingId(clientTrackingId);

        var savingItem = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, remoteEventId).ConfigureAwait(false);
        var isNewItem = savingItem == null;
        var savingItemId = isNewItem ? Guid.NewGuid() : savingItem.Id;
        savingItem ??= new CalendarItem { Id = savingItemId };

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

        savingItem.Recurrence = null;
        savingItem.RecurringCalendarItemId = null;

        savingItem.Visibility = appointment.Sensitivity switch
        {
            Sensitivity.Personal or Sensitivity.Private => CalendarItemVisibility.Private,
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

        return
        [
            new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                DurationInSeconds = appointment.ReminderMinutesBeforeStart * 60L,
                ReminderType = CalendarItemReminderType.Popup
            }
        ];
    }
}

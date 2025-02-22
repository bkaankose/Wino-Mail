using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Google.Apis.Calendar.v3.Data;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Extensions;
using Wino.Services;
using CalendarEventAttendee = Wino.Core.Domain.Entities.Calendar.CalendarEventAttendee;
using CalendarItem = Wino.Core.Domain.Entities.Calendar.CalendarItem;

namespace Wino.Core.Integration.Processors;

public class GmailChangeProcessor : DefaultChangeProcessor, IGmailChangeProcessor
{
    public GmailChangeProcessor(IDatabaseService databaseService,
                                IFolderService folderService,
                                IMailService mailService,
                                ICalendarService calendarService,
                                IAccountService accountService,
                                IMimeFileService mimeFileService) : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService)
    {

    }

    public Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId)
        => MailService.MapLocalDraftAsync(mailCopyId, newDraftId, newThreadId);

    public Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
        => MailService.CreateAssignmentAsync(accountId, mailCopyId, remoteFolderId);

    public async Task ManageCalendarEventAsync(Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount)
    {
        var status = calendarEvent.Status;

        var recurringEventId = calendarEvent.RecurringEventId;

        // 1. Canceled exceptions of recurred events are only guaranteed to have recurringEventId, Id and start time.
        // 2. Updated exceptions of recurred events have different Id, but recurringEventId is the same as parent.

        // Check if we have this event before.
        var existingCalendarItem = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, calendarEvent.Id);

        if (existingCalendarItem == null)
        {
            CalendarItem parentRecurringEvent = null;

            // Manage the recurring event id.
            if (!string.IsNullOrEmpty(recurringEventId))
            {
                parentRecurringEvent = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, recurringEventId).ConfigureAwait(false);

                if (parentRecurringEvent == null)
                {
                    Log.Information($"Parent recurring event is missing for event. Skipping creation of {calendarEvent.Id}");
                    return;
                }
            }

            // We don't have this event yet. Create a new one.
            var eventStartDateTimeOffset = GoogleIntegratorExtensions.GetEventDateTimeOffset(calendarEvent.Start);
            var eventEndDateTimeOffset = GoogleIntegratorExtensions.GetEventDateTimeOffset(calendarEvent.End);

            double totalDurationInSeconds = 0;

            if (eventStartDateTimeOffset != null && eventEndDateTimeOffset != null)
            {
                totalDurationInSeconds = (eventEndDateTimeOffset.Value - eventStartDateTimeOffset.Value).TotalSeconds;
            }

            CalendarItem calendarItem = null;

            if (parentRecurringEvent != null)
            {
                // Exceptions of parent events might not have all the fields populated.
                // We must use the parent event's data for fields that don't exists.

                // Update duration if it's not populated.
                if (totalDurationInSeconds == 0)
                {
                    totalDurationInSeconds = parentRecurringEvent.DurationInSeconds;
                }

                var organizerMail = GetOrganizerEmail(calendarEvent, organizerAccount);
                var organizerName = GetOrganizerName(calendarEvent, organizerAccount);


                calendarItem = new CalendarItem()
                {
                    CalendarId = assignedCalendar.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Description = calendarEvent.Description ?? parentRecurringEvent.Description,
                    Id = Guid.NewGuid(),
                    StartDate = eventStartDateTimeOffset.Value.DateTime,
                    StartDateOffset = eventStartDateTimeOffset.Value.Offset,
                    EndDateOffset = eventEndDateTimeOffset?.Offset ?? parentRecurringEvent.EndDateOffset,
                    DurationInSeconds = totalDurationInSeconds,
                    Location = string.IsNullOrEmpty(calendarEvent.Location) ? parentRecurringEvent.Location : calendarEvent.Location,

                    // Leave it empty if it's not populated.
                    Recurrence = GoogleIntegratorExtensions.GetRecurrenceString(calendarEvent) == null ? string.Empty : GoogleIntegratorExtensions.GetRecurrenceString(calendarEvent),
                    Status = GetStatus(calendarEvent.Status),
                    Title = string.IsNullOrEmpty(calendarEvent.Summary) ? parentRecurringEvent.Title : calendarEvent.Summary,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Visibility = string.IsNullOrEmpty(calendarEvent.Visibility) ? parentRecurringEvent.Visibility : GetVisibility(calendarEvent.Visibility),
                    HtmlLink = string.IsNullOrEmpty(calendarEvent.HtmlLink) ? parentRecurringEvent.HtmlLink : calendarEvent.HtmlLink,
                    RemoteEventId = calendarEvent.Id,
                    IsLocked = calendarEvent.Locked.GetValueOrDefault(),
                    OrganizerDisplayName = string.IsNullOrEmpty(organizerName) ? parentRecurringEvent.OrganizerDisplayName : organizerName,
                    OrganizerEmail = string.IsNullOrEmpty(organizerMail) ? parentRecurringEvent.OrganizerEmail : organizerMail
                };
            }
            else
            {
                // This is a parent event creation.
                // Start-End dates are guaranteed to be populated.

                if (eventStartDateTimeOffset == null || eventEndDateTimeOffset == null)
                {
                    Log.Error("Failed to create parent event because either start or end date is not specified.");
                    return;
                }

                calendarItem = new CalendarItem()
                {
                    CalendarId = assignedCalendar.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Description = calendarEvent.Description,
                    Id = Guid.NewGuid(),
                    StartDate = eventStartDateTimeOffset.Value.DateTime,
                    StartDateOffset = eventStartDateTimeOffset.Value.Offset,
                    EndDateOffset = eventEndDateTimeOffset.Value.Offset,
                    DurationInSeconds = totalDurationInSeconds,
                    Location = calendarEvent.Location,
                    Recurrence = GoogleIntegratorExtensions.GetRecurrenceString(calendarEvent),
                    Status = GetStatus(calendarEvent.Status),
                    Title = calendarEvent.Summary,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Visibility = GetVisibility(calendarEvent.Visibility),
                    HtmlLink = calendarEvent.HtmlLink,
                    RemoteEventId = calendarEvent.Id,
                    IsLocked = calendarEvent.Locked.GetValueOrDefault(),
                    OrganizerDisplayName = GetOrganizerName(calendarEvent, organizerAccount),
                    OrganizerEmail = GetOrganizerEmail(calendarEvent, organizerAccount)
                };
            }

            // Hide canceled events.
            calendarItem.IsHidden = calendarItem.Status == CalendarItemStatus.Cancelled;

            // Manage the recurring event id.
            if (parentRecurringEvent != null)
            {
                calendarItem.RecurringCalendarItemId = parentRecurringEvent.Id;
            }

            Debug.WriteLine($"({assignedCalendar.Name}) {calendarItem.Title}, Start: {calendarItem.StartDate.ToString("f")}, End: {calendarItem.EndDate.ToString("f")}");

            // Attendees
            var attendees = new List<CalendarEventAttendee>();

            if (calendarEvent.Attendees == null)
            {
                // Self-only event.

                attendees.Add(new CalendarEventAttendee()
                {
                    CalendarItemId = calendarItem.Id,
                    IsOrganizer = true,
                    Email = organizerAccount.Address,
                    Name = organizerAccount.SenderName,
                    AttendenceStatus = AttendeeStatus.Accepted,
                    Id = Guid.NewGuid(),
                    IsOptionalAttendee = false,
                });
            }
            else
            {
                foreach (var attendee in calendarEvent.Attendees)
                {
                    if (attendee.Self == true)
                    {
                        // TODO: 
                    }
                    else if (!string.IsNullOrEmpty(attendee.Email))
                    {
                        AttendeeStatus GetAttendenceStatus(string responseStatus)
                        {
                            return responseStatus switch
                            {
                                "accepted" => AttendeeStatus.Accepted,
                                "declined" => AttendeeStatus.Declined,
                                "tentative" => AttendeeStatus.Tentative,
                                "needsAction" => AttendeeStatus.NeedsAction,
                                _ => AttendeeStatus.NeedsAction
                            };
                        }

                        var eventAttendee = new CalendarEventAttendee()
                        {
                            CalendarItemId = calendarItem.Id,
                            IsOrganizer = attendee.Organizer ?? false,
                            Comment = attendee.Comment,
                            Email = attendee.Email,
                            Name = attendee.DisplayName,
                            AttendenceStatus = GetAttendenceStatus(attendee.ResponseStatus),
                            Id = Guid.NewGuid(),
                            IsOptionalAttendee = attendee.Optional ?? false,
                        };

                        attendees.Add(eventAttendee);
                    }
                }
            }

            await CalendarService.CreateNewCalendarItemAsync(calendarItem, attendees);
        }
        else
        {
            // We have this event already. Update it.
            if (calendarEvent.Status == "cancelled")
            {
                // Parent event is canceled. We must delete everything.
                if (string.IsNullOrEmpty(recurringEventId))
                {
                    Log.Information("Parent event is canceled. Deleting all instances of {Id}", existingCalendarItem.Id);

                    await CalendarService.DeleteCalendarItemAsync(existingCalendarItem.Id).ConfigureAwait(false);

                    return;
                }
                else
                {
                    // Child event is canceled.
                    // Child should live as long as parent lives, but must not be displayed to the user.

                    existingCalendarItem.IsHidden = true;
                }
            }
            else
            {
                // Make sure to unhide the event.
                // It might be marked as hidden before.
                existingCalendarItem.IsHidden = false;

                // Update the event properties.
            }
        }

        // Upsert the event.
        await Connection.InsertOrReplaceAsync(existingCalendarItem);
    }

    private string GetOrganizerName(Event calendarEvent, MailAccount account)
    {
        if (calendarEvent.Organizer == null) return string.Empty;

        if (calendarEvent.Organizer.Self == true)
        {
            return account.SenderName;
        }
        else
            return calendarEvent.Organizer.DisplayName;
    }

    private string GetOrganizerEmail(Event calendarEvent, MailAccount account)
    {
        if (calendarEvent.Organizer == null) return string.Empty;

        if (calendarEvent.Organizer.Self == true)
        {
            return account.Address;
        }
        else
            return calendarEvent.Organizer.Email;
    }

    private CalendarItemStatus GetStatus(string status)
    {
        return status switch
        {
            "confirmed" => CalendarItemStatus.Confirmed,
            "tentative" => CalendarItemStatus.Tentative,
            "cancelled" => CalendarItemStatus.Cancelled,
            _ => CalendarItemStatus.Confirmed
        };
    }

    private CalendarItemVisibility GetVisibility(string visibility)
    {
        /// Visibility of the event. Optional. Possible values are:   - "default" - Uses the default visibility for
        /// events on the calendar. This is the default value.  - "public" - The event is public and event details are
        /// visible to all readers of the calendar.  - "private" - The event is private and only event attendees may
        /// view event details.  - "confidential" - The event is private. This value is provided for compatibility
        /// reasons.

        return visibility switch
        {
            "default" => CalendarItemVisibility.Default,
            "public" => CalendarItemVisibility.Public,
            "private" => CalendarItemVisibility.Private,
            "confidential" => CalendarItemVisibility.Confidential,
            _ => CalendarItemVisibility.Default
        };
    }

    public Task<bool> HasAccountAnyDraftAsync(Guid accountId)
        => MailService.HasAccountAnyDraftAsync(accountId);
}

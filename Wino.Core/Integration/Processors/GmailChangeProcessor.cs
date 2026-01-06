using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Calendar.v3.Data;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
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
                    StartDate = eventStartDateTimeOffset.Value.UtcDateTime,
                    DurationInSeconds = totalDurationInSeconds,
                    Location = string.IsNullOrEmpty(calendarEvent.Location) ? parentRecurringEvent.Location : calendarEvent.Location,

                    // Store timezone information
                    StartTimeZone = GoogleIntegratorExtensions.GetEventTimeZone(calendarEvent.Start) ?? parentRecurringEvent.StartTimeZone,
                    EndTimeZone = GoogleIntegratorExtensions.GetEventTimeZone(calendarEvent.End) ?? parentRecurringEvent.EndTimeZone,

                    // Leave it empty if it's not populated.
                    Recurrence = GoogleIntegratorExtensions.GetRecurrenceString(calendarEvent) == null ? string.Empty : GoogleIntegratorExtensions.GetRecurrenceString(calendarEvent),
                    Status = GetStatus(calendarEvent.Status),
                    Title = string.IsNullOrEmpty(calendarEvent.Summary) ? parentRecurringEvent.Title : calendarEvent.Summary,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Visibility = string.IsNullOrEmpty(calendarEvent.Visibility) ? parentRecurringEvent.Visibility : GetVisibility(calendarEvent.Visibility),
                    ShowAs = string.IsNullOrEmpty(calendarEvent.Transparency) ? parentRecurringEvent.ShowAs : GetShowAs(calendarEvent.Transparency),
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
                    StartDate = eventStartDateTimeOffset.Value.UtcDateTime,
                    DurationInSeconds = totalDurationInSeconds,
                    Location = calendarEvent.Location,

                    // Store timezone information from Google Calendar event
                    StartTimeZone = GoogleIntegratorExtensions.GetEventTimeZone(calendarEvent.Start),
                    EndTimeZone = GoogleIntegratorExtensions.GetEventTimeZone(calendarEvent.End),

                    Recurrence = GoogleIntegratorExtensions.GetRecurrenceString(calendarEvent),
                    Status = GetStatus(calendarEvent.Status),
                    Title = calendarEvent.Summary,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Visibility = GetVisibility(calendarEvent.Visibility),
                    ShowAs = GetShowAs(calendarEvent.Transparency),
                    HtmlLink = calendarEvent.HtmlLink,
                    RemoteEventId = calendarEvent.Id,
                    IsLocked = calendarEvent.Locked.GetValueOrDefault(),
                    OrganizerDisplayName = GetOrganizerName(calendarEvent, organizerAccount),
                    OrganizerEmail = GetOrganizerEmail(calendarEvent, organizerAccount)
                };
            }

            // Hide canceled events.
            calendarItem.IsHidden = calendarItem.Status == CalendarItemStatus.Cancelled;

            // Set assigned calendar for navigation properties to work.
            calendarItem.AssignedCalendar = assignedCalendar;

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

            // Prepare reminders list from Gmail event
            List<Reminder> reminders = null;
            if (calendarEvent.Reminders?.Overrides != null && calendarEvent.Reminders.Overrides.Count > 0)
            {
                reminders = new List<Reminder>();
                foreach (var reminderOverride in calendarEvent.Reminders.Overrides)
                {
                    if (reminderOverride.Minutes.HasValue)
                    {
                        var durationInSeconds = reminderOverride.Minutes.Value * 60; // Convert minutes to seconds
                        var reminderType = reminderOverride.Method switch
                        {
                            "email" => CalendarItemReminderType.Email,
                            _ => CalendarItemReminderType.Popup
                        };

                        reminders.Add(new Reminder
                        {
                            Id = Guid.NewGuid(),
                            CalendarItemId = calendarItem.Id,
                            DurationInSeconds = durationInSeconds,
                            ReminderType = reminderType
                        });
                    }
                }
            }

            // Prepare attachments metadata from Gmail event
            List<CalendarAttachment> attachments = null;
            if (calendarEvent.Attachments != null && calendarEvent.Attachments.Count > 0)
            {
                attachments = calendarEvent.Attachments
                    .Where(a => a != null && !string.IsNullOrEmpty(a.Title))
                    .Select(a => new CalendarAttachment
                    {
                        Id = Guid.NewGuid(),
                        CalendarItemId = calendarItem.Id,
                        RemoteAttachmentId = a.FileId ?? a.FileUrl, // Gmail uses FileId or FileUrl
                        FileName = a.Title,
                        Size = 0, // Gmail API doesn't provide size in Event.Attachment
                        ContentType = a.MimeType ?? "application/octet-stream",
                        IsDownloaded = false,
                        LocalFilePath = null,
                        LastModified = DateTimeOffset.UtcNow
                    })
                    .ToList();
            }

            await CalendarService.CreateNewCalendarItemAsync(calendarItem, attendees);

            // Save reminders separately
            await CalendarService.SaveRemindersAsync(calendarItem.Id, reminders).ConfigureAwait(false);

            // Save attachments metadata separately
            if (attachments != null && attachments.Count > 0)
            {
                await CalendarService.InsertOrReplaceAttachmentsAsync(attachments).ConfigureAwait(false);
            }
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

            // Prepare reminders list from Gmail event for update
            List<Reminder> reminders = null;
            if (calendarEvent.Reminders?.Overrides != null && calendarEvent.Reminders.Overrides.Count > 0)
            {
                reminders = new List<Reminder>();
                foreach (var reminderOverride in calendarEvent.Reminders.Overrides)
                {
                    if (reminderOverride.Minutes.HasValue)
                    {
                        var durationInSeconds = reminderOverride.Minutes.Value * 60; // Convert minutes to seconds
                        var reminderType = reminderOverride.Method switch
                        {
                            "email" => CalendarItemReminderType.Email,
                            _ => CalendarItemReminderType.Popup
                        };

                        reminders.Add(new Reminder
                        {
                            Id = Guid.NewGuid(),
                            CalendarItemId = existingCalendarItem.Id,
                            DurationInSeconds = durationInSeconds,
                            ReminderType = reminderType
                        });
                    }
                }
            }

            // Save reminders
            await CalendarService.SaveRemindersAsync(existingCalendarItem.Id, reminders).ConfigureAwait(false);

            // Prepare attachments metadata from Gmail event for update
            List<CalendarAttachment> attachments = null;
            if (calendarEvent.Attachments != null && calendarEvent.Attachments.Count > 0)
            {
                attachments = calendarEvent.Attachments
                    .Where(a => a != null && !string.IsNullOrEmpty(a.Title))
                    .Select(a => new CalendarAttachment
                    {
                        Id = Guid.NewGuid(),
                        CalendarItemId = existingCalendarItem.Id,
                        RemoteAttachmentId = a.FileId ?? a.FileUrl,
                        FileName = a.Title,
                        Size = 0,
                        ContentType = a.MimeType ?? "application/octet-stream",
                        IsDownloaded = false,
                        LocalFilePath = null,
                        LastModified = DateTimeOffset.UtcNow
                    })
                    .ToList();
            }

            // Save attachments metadata
            if (attachments != null && attachments.Count > 0)
            {
                await CalendarService.InsertOrReplaceAttachmentsAsync(attachments).ConfigureAwait(false);
            }
        }

        // Upsert the event.
        await Connection.InsertOrReplaceAsync(existingCalendarItem, typeof(CalendarItem));
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
            "confirmed" => CalendarItemStatus.Accepted,
            "tentative" => CalendarItemStatus.Tentative,
            "cancelled" => CalendarItemStatus.Cancelled,
            _ => CalendarItemStatus.Accepted
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

    private CalendarItemShowAs GetShowAs(string transparency)
    {
        /// Google Calendar uses "transparent" for free time (event doesn't block time) 
        /// and "opaque" for busy time (event blocks time on the calendar).
        /// If not specified, defaults to opaque (busy).

        return transparency switch
        {
            "transparent" => CalendarItemShowAs.Free,
            "opaque" => CalendarItemShowAs.Busy,
            _ => CalendarItemShowAs.Busy
        };
    }

    public Task<bool> HasAccountAnyDraftAsync(Guid accountId)
        => MailService.HasAccountAnyDraftAsync(accountId);

    public Task<GmailArchiveComparisonResult> GetGmailArchiveComparisonResultAsync(Guid archiveFolderId, List<string> onlineArchiveMailIds)
        => MailService.GetGmailArchiveComparisonResultAsync(archiveFolderId, onlineArchiveMailIds);
}

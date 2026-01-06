using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graph.Models;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Extensions;
using Wino.Services;
using Reminder = Wino.Core.Domain.Entities.Calendar.Reminder;

namespace Wino.Core.Integration.Processors;

public class OutlookChangeProcessor(IDatabaseService databaseService,
                                    IFolderService folderService,
                                    ICalendarService calendarService,
                                    IMailService mailService,
                                    IAccountService accountService,
                                    IMimeFileService mimeFileService) : DefaultChangeProcessor(databaseService, folderService, mailService, calendarService, accountService, mimeFileService)
    , IOutlookChangeProcessor
{

    public Task<string> ResetAccountDeltaTokenAsync(Guid accountId)
        => AccountService.UpdateSyncIdentifierRawAsync(accountId, string.Empty);

    public async Task<string> ResetFolderDeltaTokenAsync(Guid folderId)
    {
        var folder = await FolderService.GetFolderAsync(folderId);

        folder.DeltaToken = null;

        await FolderService.UpdateFolderAsync(folder);

        return string.Empty;
    }

    public Task UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string synchronizationIdentifier)
        => Connection.ExecuteAsync("UPDATE MailItemFolder SET DeltaToken = ? WHERE Id = ?", synchronizationIdentifier, folderId);

    public async Task ManageCalendarEventAsync(Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount)
    {
        // All event types are now handled: SingleInstance, SeriesMaster, Occurrence, and Exception.
        // Occurrences from CalendarView are individual instances that are saved separately.

        var savingItem = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, calendarEvent.Id);

        Guid savingItemId = Guid.Empty;
        bool isNewItem = savingItem == null;

        if (savingItem != null)
            savingItemId = savingItem.Id;
        else
        {
            savingItemId = Guid.NewGuid();
            savingItem = new CalendarItem() { Id = savingItemId };
        }

        DateTimeOffset eventStartDateTimeOffset = OutlookIntegratorExtensions.GetDateTimeOffsetFromDateTimeTimeZone(calendarEvent.Start);
        DateTimeOffset eventEndDateTimeOffset = OutlookIntegratorExtensions.GetDateTimeOffsetFromDateTimeTimeZone(calendarEvent.End);

        var durationInSeconds = (eventEndDateTimeOffset - eventStartDateTimeOffset).TotalSeconds;

        // Store dates as UTC in the database
        savingItem.RemoteEventId = calendarEvent.Id;
        savingItem.StartDate = eventStartDateTimeOffset.UtcDateTime;
        savingItem.DurationInSeconds = durationInSeconds;

        // Store the timezone information from the event
        // This preserves the original timezone from Outlook, allowing proper reconstruction later
        // If no timezone is provided, null will indicate UTC
        savingItem.StartTimeZone = calendarEvent.Start?.TimeZone;
        savingItem.EndTimeZone = calendarEvent.End?.TimeZone;

        savingItem.Title = calendarEvent.Subject;
        savingItem.Description = calendarEvent.Body?.Content;
        savingItem.Location = calendarEvent.Location?.DisplayName;

        // Handle recurring event relationships for both Exception and Occurrence types
        if ((calendarEvent.Type == EventType.Exception || calendarEvent.Type == EventType.Occurrence) 
            && !string.IsNullOrEmpty(calendarEvent.SeriesMasterId))
        {
            // This is a recurring event instance (either an exception or a regular occurrence).
            // Link it to the parent series master.

            var parentEvent = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, calendarEvent.SeriesMasterId);

            if (parentEvent != null)
            {
                savingItem.RecurringCalendarItemId = parentEvent.Id;
            }
            else
            {
                // Parent not found yet - this can happen if occurrences sync before the series master.
                // We still save the event but without the parent link for now.
                Log.Warning($"Parent recurring event (SeriesMasterId: {calendarEvent.SeriesMasterId}) not found for event {calendarEvent.Id}. Event will be saved without parent link.");
            }
        }

        // Convert the recurrence pattern to string for parent recurring events.
        // Note: We store this for reference but don't use it to calculate occurrences.
        if (calendarEvent.Type == EventType.SeriesMaster && calendarEvent.Recurrence != null)
        {
            savingItem.Recurrence = OutlookIntegratorExtensions.ToRfc5545RecurrenceString(calendarEvent.Recurrence);
        }

        savingItem.HtmlLink = calendarEvent.WebLink;
        savingItem.CalendarId = assignedCalendar.Id;
        savingItem.OrganizerEmail = calendarEvent.Organizer?.EmailAddress?.Address;
        savingItem.OrganizerDisplayName = calendarEvent.Organizer?.EmailAddress?.Name;
        savingItem.IsHidden = false;

        // Set timestamps
        if (calendarEvent.CreatedDateTime.HasValue)
            savingItem.CreatedAt = calendarEvent.CreatedDateTime.Value;

        if (calendarEvent.LastModifiedDateTime.HasValue)
            savingItem.UpdatedAt = calendarEvent.LastModifiedDateTime.Value;

        // Set visibility
        if (calendarEvent.Sensitivity != null)
        {
            savingItem.Visibility = calendarEvent.Sensitivity.Value switch
            {
                Sensitivity.Normal => CalendarItemVisibility.Public,
                Sensitivity.Personal => CalendarItemVisibility.Private,
                Sensitivity.Private => CalendarItemVisibility.Private,
                Sensitivity.Confidential => CalendarItemVisibility.Confidential,
                _ => CalendarItemVisibility.Public
            };
        }
        else
        {
            savingItem.Visibility = CalendarItemVisibility.Public;
        }

        // Set ShowAs status
        if (calendarEvent.ShowAs != null)
        {
            savingItem.ShowAs = calendarEvent.ShowAs.Value switch
            {
                Microsoft.Graph.Models.FreeBusyStatus.Free => CalendarItemShowAs.Free,
                Microsoft.Graph.Models.FreeBusyStatus.Tentative => CalendarItemShowAs.Tentative,
                Microsoft.Graph.Models.FreeBusyStatus.Busy => CalendarItemShowAs.Busy,
                Microsoft.Graph.Models.FreeBusyStatus.Oof => CalendarItemShowAs.OutOfOffice,
                Microsoft.Graph.Models.FreeBusyStatus.WorkingElsewhere => CalendarItemShowAs.WorkingElsewhere,
                _ => CalendarItemShowAs.Busy
            };
        }
        else
        {
            savingItem.ShowAs = CalendarItemShowAs.Busy;
        }

        // Set IsLocked based on whether the user is the organizer
        // Read-only events are those where the current user is not the organizer
        savingItem.IsLocked = calendarEvent.IsOrganizer.HasValue && !calendarEvent.IsOrganizer.Value;

        if (calendarEvent.ResponseStatus?.Response != null)
        {
            switch (calendarEvent.ResponseStatus.Response.Value)
            {
                case ResponseType.None:
                case ResponseType.NotResponded:
                    savingItem.Status = CalendarItemStatus.NotResponded;
                    break;
                case ResponseType.TentativelyAccepted:
                    savingItem.Status = CalendarItemStatus.Tentative;
                    break;
                case ResponseType.Accepted:
                case ResponseType.Organizer:
                    savingItem.Status = CalendarItemStatus.Accepted;
                    break;
                case ResponseType.Declined:
                    savingItem.Status = CalendarItemStatus.Cancelled;
                    savingItem.IsHidden = true;
                    break;
                default:
                    break;
            }
        }
        else
        {
            savingItem.Status = CalendarItemStatus.Accepted;
        }

        // Prepare attendees list
        List<CalendarEventAttendee> attendees = null;
        if (calendarEvent.Attendees != null)
        {
            // Pass the organizer's email address to properly identify the organizer in the attendees list
            string organizerEmail = calendarEvent.Organizer?.EmailAddress?.Address;
            attendees = calendarEvent.Attendees.Select(a => a.CreateAttendee(savingItemId, organizerEmail)).ToList();
        }

        // Prepare reminders list from Outlook event
        List<Reminder> reminders = null;
        if (calendarEvent.IsReminderOn.GetValueOrDefault() && calendarEvent.ReminderMinutesBeforeStart.HasValue)
        {
            var reminderMinutes = calendarEvent.ReminderMinutesBeforeStart.Value;
            var reminderDurationInSeconds = reminderMinutes * 60; // Convert minutes to seconds

            reminders = new List<Reminder>
            {
                new Reminder
                {
                    Id = Guid.NewGuid(),
                    CalendarItemId = savingItemId,
                    DurationInSeconds = reminderDurationInSeconds,
                    ReminderType = CalendarItemReminderType.Popup
                }
            };
        }

        // Prepare attachments metadata from Outlook event
        List<CalendarAttachment> attachments = null;
        if (calendarEvent.HasAttachments.GetValueOrDefault() && calendarEvent.Attachments != null)
        {
            attachments = calendarEvent.Attachments
                .Where(a => a != null && !string.IsNullOrEmpty(a.Name))
                .Select(a => new CalendarAttachment
                {
                    Id = Guid.NewGuid(),
                    CalendarItemId = savingItemId,
                    RemoteAttachmentId = a.Id,
                    FileName = a.Name,
                    Size = a.Size ?? 0,
                    ContentType = a.ContentType ?? "application/octet-stream",
                    IsDownloaded = false,
                    LocalFilePath = null,
                    LastModified = calendarEvent.LastModifiedDateTime ?? DateTimeOffset.UtcNow
                })
                .ToList();
        }

        // Set assigned calendar for navigation properties to work.
        savingItem.AssignedCalendar = assignedCalendar;

        // Use CalendarService to create or update the event
        if (isNewItem)
        {
            // New item - use CreateNewCalendarItemAsync
            await CalendarService.CreateNewCalendarItemAsync(savingItem, attendees).ConfigureAwait(false);
        }
        else
        {
            // Existing item - use UpdateCalendarItemAsync
            await CalendarService.UpdateCalendarItemAsync(savingItem, attendees).ConfigureAwait(false);
        }

        // Save reminders separately
        await CalendarService.SaveRemindersAsync(savingItemId, reminders).ConfigureAwait(false);

        // Save attachments metadata separately
        if (attachments != null && attachments.Count > 0)
        {
            await CalendarService.InsertOrReplaceAttachmentsAsync(attachments).ConfigureAwait(false);
        }
    }
}

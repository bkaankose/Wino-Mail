using System;
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

namespace Wino.Core.Integration.Processors
{
    public class OutlookChangeProcessor(IDatabaseService databaseService,
                                        IFolderService folderService,
                                        ICalendarService calendarService,
                                        IMailService mailService,
                                        IAccountService accountService,
                                        IMimeFileService mimeFileService) : DefaultChangeProcessor(databaseService, folderService, mailService, calendarService, accountService, mimeFileService)
        , IOutlookChangeProcessor
    {
        public Task<bool> IsMailExistsAsync(string messageId)
            => MailService.IsMailExistsAsync(messageId);

        public Task<bool> IsMailExistsInFolderAsync(string messageId, Guid folderId)
            => MailService.IsMailExistsAsync(messageId, folderId);

        public Task<string> ResetAccountDeltaTokenAsync(Guid accountId)
            => AccountService.UpdateSynchronizationIdentifierAsync(accountId, null);

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
            // We parse the occurrences based on the parent event.
            // There is literally no point to store them because
            // type=Exception events are the exceptional childs of recurrency parent event.

            if (calendarEvent.Type == EventType.Occurrence) return;

            var savingItem = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, calendarEvent.Id);

            Guid savingItemId = Guid.Empty;

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

            savingItem.RemoteEventId = calendarEvent.Id;
            savingItem.StartDate = eventStartDateTimeOffset.DateTime;
            savingItem.StartDateOffset = eventStartDateTimeOffset.Offset;
            savingItem.EndDateOffset = eventEndDateTimeOffset.Offset;
            savingItem.DurationInSeconds = durationInSeconds;

            savingItem.Title = calendarEvent.Subject;
            savingItem.Description = calendarEvent.Body?.Content;
            savingItem.Location = calendarEvent.Location?.DisplayName;

            if (calendarEvent.Type == EventType.Exception && !string.IsNullOrEmpty(calendarEvent.SeriesMasterId))
            {
                // This is a recurring event exception.
                // We need to find the parent event and set it as recurring event id.

                var parentEvent = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, calendarEvent.SeriesMasterId);

                if (parentEvent != null)
                {
                    savingItem.RecurringCalendarItemId = parentEvent.Id;
                }
                else
                {
                    Log.Warning($"Parent recurring event is missing for event. Skipping creation of {calendarEvent.Id}");
                    return;
                }
            }

            // Convert the recurrence pattern to string for parent recurring events.
            if (calendarEvent.Type == EventType.SeriesMaster && calendarEvent.Recurrence != null)
            {
                savingItem.Recurrence = OutlookIntegratorExtensions.ToRfc5545RecurrenceString(calendarEvent.Recurrence);
            }

            savingItem.HtmlLink = calendarEvent.WebLink;
            savingItem.CalendarId = assignedCalendar.Id;
            savingItem.OrganizerEmail = calendarEvent.Organizer?.EmailAddress?.Address;
            savingItem.OrganizerDisplayName = calendarEvent.Organizer?.EmailAddress?.Name;
            savingItem.IsHidden = false;

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
                        savingItem.Status = CalendarItemStatus.Confirmed;
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
                savingItem.Status = CalendarItemStatus.Confirmed;
            }

            // Upsert the event.
            await Connection.InsertOrReplaceAsync(savingItem);

            // Manage attendees.
            if (calendarEvent.Attendees != null)
            {
                // Clear all attendees for this event.
                var attendees = calendarEvent.Attendees.Select(a => a.CreateAttendee(savingItemId)).ToList();
                await CalendarService.ManageEventAttendeesAsync(savingItemId, attendees).ConfigureAwait(false);
            }
        }
    }
}

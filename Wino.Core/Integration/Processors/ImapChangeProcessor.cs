using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

public class ImapChangeProcessor : DefaultChangeProcessor, IImapChangeProcessor
{
    private readonly ICalendarIcsFileService _calendarIcsFileService;

    public ImapChangeProcessor(IDatabaseService databaseService,
                               IFolderService folderService,
                               IMailService mailService,
                               IAccountService accountService,
                               ICalendarService calendarService,
                               IMimeFileService mimeFileService,
                               ICalendarIcsFileService calendarIcsFileService) : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService)
    {
        _calendarIcsFileService = calendarIcsFileService;
    }

    public Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId) => FolderService.GetKnownUidsForFolderAsync(folderId);

    public Task<IEnumerable<string>> GetRecentMailIdsForFolderAsync(Guid folderId, int count)
        => MailService.GetRecentMailIdsForFolderAsync(folderId, count);

    public async Task ManageCalendarEventAsync(CalDavCalendarEvent calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount)
    {
        if (calendarEvent == null || assignedCalendar == null)
            return;

        var existingItem = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, calendarEvent.RemoteEventId).ConfigureAwait(false);
        var isNewItem = existingItem == null;
        var savingItemId = existingItem?.Id ?? Guid.NewGuid();
        var savingItem = existingItem ?? new CalendarItem { Id = savingItemId };

        var start = calendarEvent.Start.UtcDateTime;
        var end = calendarEvent.End.UtcDateTime;

        if (end <= start)
            end = start.AddHours(1);

        savingItem.RemoteEventId = calendarEvent.RemoteEventId;
        savingItem.CalendarId = assignedCalendar.Id;
        savingItem.StartDate = start;
        savingItem.DurationInSeconds = (end - start).TotalSeconds;
        savingItem.StartTimeZone = calendarEvent.StartTimeZone;
        savingItem.EndTimeZone = calendarEvent.EndTimeZone;
        savingItem.Title = calendarEvent.Title;
        savingItem.Description = calendarEvent.Description;
        savingItem.Location = calendarEvent.Location;
        savingItem.Recurrence = calendarEvent.Recurrence;
        savingItem.Status = calendarEvent.Status;
        savingItem.Visibility = calendarEvent.Visibility;
        savingItem.ShowAs = calendarEvent.ShowAs;
        savingItem.IsHidden = calendarEvent.IsHidden;
        savingItem.HtmlLink = string.Empty;
        savingItem.IsLocked = false;
        savingItem.OrganizerDisplayName = !string.IsNullOrWhiteSpace(calendarEvent.OrganizerDisplayName)
            ? calendarEvent.OrganizerDisplayName
            : organizerAccount?.SenderName ?? string.Empty;
        savingItem.OrganizerEmail = !string.IsNullOrWhiteSpace(calendarEvent.OrganizerEmail)
            ? calendarEvent.OrganizerEmail
            : organizerAccount?.Address ?? string.Empty;
        savingItem.AssignedCalendar = assignedCalendar;

        if (savingItem.CreatedAt == default)
            savingItem.CreatedAt = DateTimeOffset.UtcNow;

        savingItem.UpdatedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(calendarEvent.SeriesMasterRemoteEventId))
        {
            var parentEvent = await CalendarService
                .GetCalendarItemAsync(assignedCalendar.Id, calendarEvent.SeriesMasterRemoteEventId)
                .ConfigureAwait(false);

            if (parentEvent != null)
            {
                savingItem.RecurringCalendarItemId = parentEvent.Id;
            }
        }
        else
        {
            savingItem.RecurringCalendarItemId = null;
        }

        var attendees = calendarEvent.Attendees?
            .Where(a => !string.IsNullOrWhiteSpace(a.Email))
            .Select(a => new CalendarEventAttendee
            {
                Id = Guid.NewGuid(),
                CalendarItemId = savingItemId,
                Name = a.Name,
                Email = a.Email,
                AttendenceStatus = a.AttendenceStatus,
                IsOrganizer = a.IsOrganizer,
                IsOptionalAttendee = a.IsOptionalAttendee
            })
            .ToList();

        var reminders = calendarEvent.Reminders?
            .Where(r => r.DurationInSeconds > 0)
            .Select(r => new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = savingItemId,
                DurationInSeconds = r.DurationInSeconds,
                ReminderType = r.ReminderType
            })
            .ToList();

        if (isNewItem)
        {
            await CalendarService.CreateNewCalendarItemAsync(savingItem, attendees).ConfigureAwait(false);
        }
        else
        {
            await CalendarService.UpdateCalendarItemAsync(savingItem, attendees).ConfigureAwait(false);
        }

        await CalendarService.SaveRemindersAsync(savingItemId, reminders).ConfigureAwait(false);
    }

    public Task SaveCalendarItemIcsAsync(Guid accountId, Guid calendarId, Guid calendarItemId, string remoteEventId, string remoteResourceHref, string eTag, string icsContent)
        => _calendarIcsFileService.SaveCalendarItemIcsAsync(accountId, calendarId, calendarItemId, remoteEventId, remoteResourceHref, eTag, icsContent);

    public Task DeleteCalendarItemIcsAsync(Guid accountId, Guid calendarItemId)
        => _calendarIcsFileService.DeleteCalendarItemIcsAsync(accountId, calendarItemId);

    public Task DeleteCalendarIcsForCalendarAsync(Guid accountId, Guid calendarId)
        => _calendarIcsFileService.DeleteCalendarIcsForCalendarAsync(accountId, calendarId);

    public override async Task DeleteCalendarItemAsync(Guid calendarItemId)
    {
        var item = await CalendarService.GetCalendarItemAsync(calendarItemId).ConfigureAwait(false);
        if (item == null)
            return;

        await _calendarIcsFileService.DeleteCalendarItemIcsAsync(item.AssignedCalendar?.AccountId ?? Guid.Empty, calendarItemId).ConfigureAwait(false);
        await base.DeleteCalendarItemAsync(calendarItemId).ConfigureAwait(false);
    }

    public override async Task DeleteCalendarItemAsync(string calendarRemoteEventId, Guid calendarId)
    {
        var item = await CalendarService.GetCalendarItemAsync(calendarId, calendarRemoteEventId).ConfigureAwait(false);
        if (item == null)
            return;

        await DeleteCalendarItemAsync(item.Id).ConfigureAwait(false);
    }
}

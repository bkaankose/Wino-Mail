using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

/// <summary>
/// Change processor for on-premises Exchange accounts, shared by the EWS and MAPI over HTTP synchronizers. The mail
/// surface is the default one; this adds persisting the transport a synchronizer detected and the provider-neutral
/// calendar upsert both transports feed. Contacts and tasks go through the contact and task services, as the Outlook
/// and Gmail synchronizers do.
/// </summary>
public interface IExchangeChangeProcessor : IDefaultChangeProcessor
{
    /// <summary>
    /// Persists server information a synchronizer changed at run time, such as the detected transport when a MAPI
    /// over HTTP attempt learns that the protocol is not offered.
    /// </summary>
    Task UpdateAccountServerInformationAsync(CustomServerInformation serverInformation);

    /// <summary>Upserts one occurrence, or a series master, pulled from the server into the calendar.</summary>
    Task ManageCalendarEventAsync(SyncedCalendarEvent occurrence, AccountCalendar assignedCalendar, MailAccount organizerAccount);

    /// <summary>The stored occurrences of a calendar inside a UTC window, for deletion reconciliation.</summary>
    Task<List<CalendarItem>> GetCalendarItemsInRangeAsync(AccountCalendar calendar, DateTime startUtc, DateTime endUtc);

    /// <summary>The series masters stored for a calendar, for deletion reconciliation.</summary>
    Task<List<CalendarItem>> GetRecurringMastersAsync(AccountCalendar calendar);
}

public class ExchangeChangeProcessor : DefaultChangeProcessor, IExchangeChangeProcessor
{
    public ExchangeChangeProcessor(IDatabaseService databaseService,
                                   IFolderService folderService,
                                   IMailService mailService,
                                   ICalendarService calendarService,
                                   IAccountService accountService,
                                   IMimeFileService mimeFileService,
                                   IContactService? contactService = null,
                                   ITaskService? taskService = null)
        : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService, contactService, taskService)
    {
    }

    public Task UpdateAccountServerInformationAsync(CustomServerInformation serverInformation)
        => AccountService.UpdateAccountCustomServerInformationAsync(serverInformation);

    public Task<List<CalendarItem>> GetCalendarItemsInRangeAsync(AccountCalendar calendar, DateTime startUtc, DateTime endUtc)
        => CalendarService.GetCalendarEventsAsync(calendar, new TimeRange(startUtc, endUtc));

    public Task<List<CalendarItem>> GetRecurringMastersAsync(AccountCalendar calendar)
        => CalendarService.GetRecurringMastersAsync(calendar.Id);

    public async Task ManageCalendarEventAsync(SyncedCalendarEvent occurrence, AccountCalendar assignedCalendar, MailAccount organizerAccount)
    {
        var savingItem = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, occurrence.RemoteId).ConfigureAwait(false);

        // A create stamps the local preview id on the server item. The server's id is correlated with the original
        // local row first, so the optimistic item is adopted rather than duplicated, as the Outlook processor does
        // with its transaction id.
        var clientTrackingId = occurrence.RemoteId.GetClientTrackingId();
        if (clientTrackingId.HasValue && string.IsNullOrEmpty(occurrence.RecurringParentRemoteId))
        {
            var createdItem = await CalendarService.GetCalendarItemAsync(clientTrackingId.Value).ConfigureAwait(false);
            if (createdItem?.CalendarId == assignedCalendar.Id &&
                createdItem.RemoteEventId.GetClientTrackingId() == clientTrackingId)
            {
                if (savingItem != null && savingItem.Id != createdItem.Id)
                    await MergeDuplicateCalendarItemAsync(createdItem, savingItem).ConfigureAwait(false);

                savingItem = createdItem;
            }
        }

        var isNewItem = savingItem == null;
        var savingItemId = savingItem?.Id ?? Guid.NewGuid();
        savingItem ??= new CalendarItem { Id = savingItemId };

        var (startDate, ianaTimeZone) = ResolveStoredStart(occurrence);

        savingItem.RemoteEventId = occurrence.RemoteId;
        savingItem.StartDate = startDate;
        savingItem.DurationInSeconds = (occurrence.EndUtc - occurrence.StartUtc).TotalSeconds;
        savingItem.StartTimeZone = ianaTimeZone;
        savingItem.EndTimeZone = ianaTimeZone;

        savingItem.Title = occurrence.Title;
        savingItem.Description = occurrence.Description;
        savingItem.Location = occurrence.Location;
        savingItem.CalendarId = assignedCalendar.Id;
        savingItem.AssignedCalendar = assignedCalendar;
        savingItem.OrganizerEmail = occurrence.OrganizerEmail;
        savingItem.OrganizerDisplayName = occurrence.OrganizerName;
        savingItem.CreatedAt = occurrence.CreatedAt;
        savingItem.UpdatedAt = occurrence.UpdatedAt;
        savingItem.IsHidden = false;

        // A series master carries its rule and is not rendered; an occurrence links to its master. Both stay empty
        // for series the server expands itself (EWS calendar views).
        savingItem.Recurrence = string.IsNullOrEmpty(occurrence.Recurrence) ? null : occurrence.Recurrence;
        savingItem.RecurringCalendarItemId = null;
        if (!string.IsNullOrEmpty(occurrence.RecurringParentRemoteId))
        {
            var master = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, occurrence.RecurringParentRemoteId).ConfigureAwait(false);
            savingItem.RecurringCalendarItemId = master?.Id;
        }

        savingItem.Visibility = occurrence.Visibility;
        savingItem.ShowAs = occurrence.ShowAs;

        var isOrganizer = !string.IsNullOrEmpty(occurrence.OrganizerEmail) &&
                          string.Equals(occurrence.OrganizerEmail, organizerAccount?.Address, StringComparison.OrdinalIgnoreCase);
        savingItem.IsLocked = !isOrganizer;

        savingItem.Status = occurrence.MyResponse;
        if (savingItem.Status == CalendarItemStatus.Cancelled)
            savingItem.IsHidden = true;

        var attendees = BuildAttendees(occurrence.Attendees, savingItemId);

        if (isNewItem)
            await CalendarService.CreateNewCalendarItemAsync(savingItem, attendees).ConfigureAwait(false);
        else
            await CalendarService.UpdateCalendarItemAsync(savingItem, attendees).ConfigureAwait(false);

        // Provider reminders initialize newly imported events only. Once an event exists locally, Wino owns its
        // reminder list.
        if (isNewItem)
        {
            List<Reminder>? reminders = occurrence.ReminderMinutesBeforeStart is { } minutes
                ? [new Reminder { Id = Guid.NewGuid(), CalendarItemId = savingItemId, DurationInSeconds = minutes * 60L, ReminderType = CalendarItemReminderType.Popup }]
                : null;

            await CalendarService.SaveRemindersAsync(savingItemId, reminders).ConfigureAwait(false);
        }
    }

    private async Task MergeDuplicateCalendarItemAsync(CalendarItem original, CalendarItem duplicate)
    {
        // Only the client tracking id establishes identity; titles and dates do not. Local reminder choices,
        // downloaded attachments and series relationships are kept.
        await Connection.RunInTransactionAsync(connection =>
        {
            connection.Execute("UPDATE Reminder SET CalendarItemId = ? WHERE CalendarItemId = ?", original.Id, duplicate.Id);
            connection.Execute("UPDATE CalendarAttachment SET CalendarItemId = ? WHERE CalendarItemId = ?", original.Id, duplicate.Id);
            connection.Execute("UPDATE CalendarEventAttendee SET CalendarItemId = ? WHERE CalendarItemId = ?", original.Id, duplicate.Id);
            connection.Execute("UPDATE CalendarItem SET RecurringCalendarItemId = ? WHERE RecurringCalendarItemId = ?", original.Id, duplicate.Id);
            connection.Delete<CalendarItem>(duplicate.Id);
        }).ConfigureAwait(false);

        WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(duplicate, EntityUpdateSource.Server));
    }

    /// <summary>
    /// The start to store and the zone beside it: wall clock in the event's own zone, or the plain UTC value with no
    /// zone when the event names none this platform can resolve.
    /// </summary>
    private static (DateTime StartDate, string? IanaTimeZone) ResolveStoredStart(SyncedCalendarEvent occurrence)
    {
        if (string.IsNullOrEmpty(occurrence.TimeZoneIana))
            return (occurrence.StartUtc, null);

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(occurrence.TimeZoneIana);
            return (TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(occurrence.StartUtc, DateTimeKind.Utc), zone), occurrence.TimeZoneIana);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return (occurrence.StartUtc, null);
        }
    }

    private static List<CalendarEventAttendee>? BuildAttendees(List<SyncedCalendarAttendee>? source, Guid calendarItemId)
    {
        if (source is not { Count: > 0 })
            return null;

        var attendees = source
            .Where(a => !string.IsNullOrEmpty(a.Email))
            .Select(a => new CalendarEventAttendee
            {
                Id = Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                Name = a.Name,
                Email = a.Email,
                IsOptionalAttendee = a.IsOptional,
                AttendenceStatus = a.Status
            })
            .ToList();

        return attendees.Count == 0 ? null : attendees;
    }
}

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
/// Change processor for on-premises Exchange accounts (EWS and MAPI/HTTP). The mail surface is the
/// default one; this adds persisting the transport the synchronizer detected and the provider-neutral
/// calendar upsert both transports feed. Contacts and tasks go through the contact and task services
/// directly, as the Outlook and Gmail synchronizers do.
/// </summary>
public interface IExchangeChangeProcessor : IDefaultChangeProcessor
{
    /// <summary>
    /// Persists server information the synchronizer changed at run time, such as the detected transport
    /// when a MAPI/HTTP attempt learns the protocol is not offered.
    /// </summary>
    Task UpdateAccountServerInformationAsync(CustomServerInformation serverInformation);

    /// <summary>Upserts one occurrence (or a series master) pulled from the server into the calendar.</summary>
    Task ManageCalendarEventAsync(SyncedCalendarEvent occurrence, AccountCalendar assignedCalendar, MailAccount organizerAccount);

    /// <summary>The stored occurrences of a calendar inside a UTC window, for deletion reconciliation.</summary>
    Task<List<CalendarItem>> GetCalendarItemsInRangeAsync(AccountCalendar calendar, DateTime startUtc, DateTime endUtc);

    /// <summary>The series masters stored for a calendar (rows with a rule and no parent), for deletion reconciliation.</summary>
    Task<List<CalendarItem>> GetRecurringMastersAsync(AccountCalendar calendar);

    /// <summary>
    /// Merges server-side junk lists (read from the mailbox's junk rule over MAPI) into the account's
    /// local Blocked / Safe senders. One-way: local-only entries are kept. Returns the number added.
    /// </summary>
    Task<int> ImportJunkSendersAsync(Guid accountId, JunkListType listType, IEnumerable<string> addresses);
}

public class ExchangeChangeProcessor : DefaultChangeProcessor, IExchangeChangeProcessor
{
    private readonly IJunkSenderService _junkSenderService;

    public ExchangeChangeProcessor(IDatabaseService databaseService,
                                   IFolderService folderService,
                                   IMailService mailService,
                                   ICalendarService calendarService,
                                   IAccountService accountService,
                                   IMimeFileService mimeFileService,
                                   IContactService contactService = null,
                                   ITaskService taskService = null,
                                   IJunkSenderService junkSenderService = null)
        : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService, contactService, taskService)
    {
        _junkSenderService = junkSenderService;
    }

    public Task UpdateAccountServerInformationAsync(CustomServerInformation serverInformation)
        => AccountService.UpdateAccountCustomServerInformationAsync(serverInformation);

    public Task<int> ImportJunkSendersAsync(Guid accountId, JunkListType listType, IEnumerable<string> addresses)
        => _junkSenderService is null ? Task.FromResult(0) : _junkSenderService.ImportAsync(accountId, listType, addresses);

    public Task<List<CalendarItem>> GetCalendarItemsInRangeAsync(AccountCalendar calendar, DateTime startUtc, DateTime endUtc)
        => CalendarService.GetCalendarEventsAsync(calendar, new TimeRange(startUtc, endUtc));

    public Task<List<CalendarItem>> GetRecurringMastersAsync(AccountCalendar calendar)
        => CalendarService.GetRecurringMastersAsync(calendar.Id);

    public async Task ManageCalendarEventAsync(SyncedCalendarEvent occurrence, AccountCalendar assignedCalendar, MailAccount organizerAccount)
    {
        var savingItem = await CalendarService.GetCalendarItemAsync(assignedCalendar.Id, occurrence.RemoteId).ConfigureAwait(false);

        // A create stamps the local preview id on the server item. Correlate the server's id with the
        // original local row before considering this a new event, so the optimistic item is adopted
        // rather than duplicated (the Outlook processor does the same with its transaction id).
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
        var savingItemId = isNewItem ? Guid.NewGuid() : savingItem.Id;
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

        // A series master carries its rule and is not rendered; an occurrence links to its master so the
        // app can offer "view series". Both stay null for providers that expand server-side (EWS).
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

        // Provider reminders initialize newly imported events only. Once an event exists
        // locally, Wino owns its reminder list and schedules every selected reminder.
        if (isNewItem)
        {
            List<Reminder> reminders = occurrence.ReminderMinutesBeforeStart is { } minutes
                ? [new Reminder { Id = Guid.NewGuid(), CalendarItemId = savingItemId, DurationInSeconds = minutes * 60L, ReminderType = CalendarItemReminderType.Popup }]
                : null;

            await CalendarService.SaveRemindersAsync(savingItemId, reminders).ConfigureAwait(false);
        }
    }

    private async Task MergeDuplicateCalendarItemAsync(CalendarItem original, CalendarItem duplicate)
    {
        // Only the client tracking id establishes identity; titles and dates do not.
        // Keep local reminder choices, downloaded attachments and series relationships.
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
    /// The StartDate to store and the zone to store beside it: wall-clock in the event's own zone, or
    /// the plain UTC value with no zone when the event names none this platform can resolve.
    /// </summary>
    private static (DateTime StartDate, string IanaTimeZone) ResolveStoredStart(SyncedCalendarEvent occurrence)
    {
        if (string.IsNullOrEmpty(occurrence.TimeZoneIana))
            return (occurrence.StartUtc, null);

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(occurrence.TimeZoneIana);
            return (TimeZoneInfo.ConvertTimeFromUtc(occurrence.StartUtc, zone), occurrence.TimeZoneIana);
        }
        catch
        {
            return (occurrence.StartUtc, null);
        }
    }

    /// <summary>The event's attendees as rows of the calendar item, or null when it has none with an address.</summary>
    private static List<CalendarEventAttendee> BuildAttendees(List<SyncedCalendarAttendee> source, Guid calendarItemId)
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

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Core.Synchronizers.CalDav;

/// <summary>
/// Default alarms (<c>calendar-default-alarms</c>, CalendarServer extension): the server stores
/// the VALARM a client applies to new events that have none, per calendar or for the whole home.
/// Like Apple Calendar, Mail-Cal writes it into events it creates (marked with
/// <c>X-APPLE-DEFAULT-ALARM:TRUE</c>) and seeds the local reminders of newly imported events
/// without alarms. Timed events use <c>default-alarm-vevent-datetime</c>, all-day events
/// <c>default-alarm-vevent-date</c>.
/// </summary>
public sealed partial class CalDavSynchronizationEngine
{
    // Default alarms chosen in CreateEventAsync, written into the ICS right before its PUT.
    private readonly ConditionalWeakTable<CalendarItem, IReadOnlyList<CalDavDefaultAlarm>> _pendingDefaultAlarms = new();

    // Parsed defaults per synchronization pass of one calendar.
    private readonly ConditionalWeakTable<CalendarContext, DefaultAlarmSet> _contextDefaultAlarms = new();

    /// <summary>
    /// The default reminders of a calendar for a new event, as stored in <see cref="ICalDavStateStore"/>.
    /// A value on the calendar wins over the home value, even when it means "no alarm". Alarms
    /// Mail-Cal cannot express locally (after the start, relative to the end) are left out.
    /// </summary>
    public static IReadOnlyList<CalDavEventReminder> GetDefaultReminders(CalDavAccountState state, string? remoteCalendarId, bool isAllDay)
    {
        ArgumentNullException.ThrowIfNull(state);

        return ResolveDefaultAlarms(state, state.FindCalendar(remoteCalendarId))
            .For(isAllDay)
            .Select(alarm => alarm.Reminder)
            .OfType<CalDavEventReminder>()
            .ToArray();
    }

    internal static DefaultAlarmSet ResolveDefaultAlarms(CalDavAccountState state, CalDavCalendarState? calendarState)
    {
        static string Pick(string? calendarValue, string accountValue)
            => string.IsNullOrWhiteSpace(calendarValue) ? accountValue : calendarValue;

        return new DefaultAlarmSet(
            CalendarIcsBuilder.ParseDefaultAlarms(Pick(calendarState?.DefaultAlarmVEventDateTime, state.DefaultAlarmVEventDateTime)),
            CalendarIcsBuilder.ParseDefaultAlarms(Pick(calendarState?.DefaultAlarmVEventDate, state.DefaultAlarmVEventDate)));
    }

    /// <summary>
    /// Import hook: an event that is new to Mail-Cal and whose resource carries no VALARM gets the
    /// calendar's default reminders. Existing items keep their local reminder choices.
    /// </summary>
    private async Task SeedDefaultRemindersAsync(
        CalendarContext context,
        CalendarItem? existingLocalItem,
        CalendarItem localItem,
        CalDavCalendarEvent remoteEvent,
        string? icsContent)
    {
        if (existingLocalItem != null || remoteEvent.Reminders?.Count > 0)
            return;

        if (!string.IsNullOrEmpty(icsContent) && icsContent.Contains("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase))
            return;

        if (!_contextDefaultAlarms.TryGetValue(context, out var defaults))
        {
            var state = await _stateStore.GetAsync(context.Account.Id).ConfigureAwait(false);
            defaults = ResolveDefaultAlarms(state, context.CalendarState);
            _contextDefaultAlarms.AddOrUpdate(context, defaults);
        }

        var reminders = CreateReminderRows(defaults.For(localItem.IsAllDayEvent), localItem.Id);
        if (reminders.Count == 0)
            return;

        await _calendarService.SaveRemindersAsync(localItem.Id, reminders).ConfigureAwait(false);
    }

    /// <summary>
    /// Create hook: when the user chose no reminder, the calendar's default alarm is written into
    /// the new resource and its local reminder rows are returned for persistence.
    /// </summary>
    private async Task<List<Reminder>?> ApplyDefaultAlarmOnCreateAsync(
        MailAccount account,
        CalendarItem item,
        List<Reminder>? reminders,
        CancellationToken cancellationToken)
    {
        if (reminders?.Count > 0)
            return reminders;

        try
        {
            var assignedCalendar = await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);
            if (assignedCalendar == null || string.IsNullOrWhiteSpace(assignedCalendar.RemoteCalendarId))
                return reminders;

            var state = await _stateStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
            var alarms = ResolveDefaultAlarms(state, state.FindCalendar(assignedCalendar.RemoteCalendarId)).For(item.IsAllDayEvent);
            if (alarms.Count == 0)
                return reminders;

            _pendingDefaultAlarms.AddOrUpdate(item, alarms);

            // PersistCreatedCalendarEventAsync assigns the final item ID to these rows.
            return CreateReminderRows(alarms, item.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A default alarm is a convenience; it must never block creating the event.
            Logger.Warning(ex, "Could not apply the CalDAV default alarm to a new event");
            return reminders;
        }
    }

    /// <summary>Writes the default alarm chosen for a new item into its ICS (create only).</summary>
    private string ApplyPendingDefaultAlarm(CalendarItem item, bool isCreate, string icsContent)
    {
        if (!_pendingDefaultAlarms.TryGetValue(item, out var alarms))
            return icsContent;

        _pendingDefaultAlarms.Remove(item);
        return isCreate ? CalendarIcsBuilder.AddDefaultAlarms(icsContent, alarms) : icsContent;
    }

    private static List<Reminder> CreateReminderRows(IReadOnlyList<CalDavDefaultAlarm> alarms, Guid calendarItemId)
        => alarms
            .Select(alarm => alarm.Reminder)
            .OfType<CalDavEventReminder>()
            .Select(reminder => new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                DurationInSeconds = reminder.DurationInSeconds,
                ReminderType = reminder.ReminderType
            })
            .ToList();

    internal sealed record DefaultAlarmSet(IReadOnlyList<CalDavDefaultAlarm> DateTime, IReadOnlyList<CalDavDefaultAlarm> Date)
    {
        public IReadOnlyList<CalDavDefaultAlarm> For(bool isAllDay) => isAllDay ? Date : DateTime;
    }
}

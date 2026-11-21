#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>The reminders of one event split into the ones its server stores and the ones kept only in Wino.</summary>
public sealed record ReminderSplit(List<Reminder> Server, List<Reminder> Device);

/// <summary>
/// Decides which reminders of an event go to its server. Reminders the server cannot store, or beyond
/// how many it keeps, stay in the local database and fire from there like the others. Nothing marks
/// which is which: the split is recomputed from the stored rows and the calendar's capabilities.
/// </summary>
public static class ReminderSyncPlan
{
    public static ReminderSplit Split(IEnumerable<Reminder>? reminders, CalendarCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var all = (reminders ?? []).Where(reminder => reminder != null).ToList();
        var eligible = all
            .Where(reminder => CanServerStore(reminder, capabilities))
            .OrderBy(reminder => Math.Abs(reminder.DurationInSeconds))
            .ThenByDescending(reminder => reminder.DurationInSeconds)
            .ThenBy(reminder => reminder.ReminderType)
            .ToList();

        var server = capabilities.MaxReminders is > 0 and var max
            ? eligible.Take(max).ToList()
            : eligible;

        var device = all.Where(reminder => !server.Contains(reminder)).ToList();
        return new ReminderSplit(server, device);
    }

    public static bool CanServerStore(Reminder reminder, CalendarCapabilities capabilities)
    {
        if (!capabilities.SupportsReminders)
            return false;

        if (reminder.ReminderType == CalendarItemReminderType.Email && !capabilities.SupportsEmailReminder)
            return false;

        if (reminder.DurationInSeconds < 0 && !capabilities.SupportsReminderAfterStart)
            return false;

        return capabilities.MaxReminderOffsetMinutes is not { } maxMinutes || reminder.DurationInSeconds / 60 <= maxMinutes;
    }

    /// <summary>
    /// The reminders to store after the server reported <paramref name="serverReminders"/> for an event:
    /// the server's set replaces the reminders the server held, and the ones kept only in Wino stay.
    /// Returns null when the stored reminders already match.
    /// </summary>
    public static List<Reminder>? Reconcile(
        IReadOnlyCollection<Reminder>? storedReminders,
        IReadOnlyCollection<Reminder>? serverReminders,
        CalendarCapabilities capabilities,
        Guid calendarItemId)
    {
        var stored = storedReminders?.ToList() ?? [];
        var device = Split(stored, capabilities).Device;
        var result = new List<Reminder>();

        foreach (var reminder in (serverReminders ?? []).Concat(device))
        {
            if (result.Any(existing => SameReminder(existing, reminder)))
                continue;

            var match = stored.FirstOrDefault(existing => SameReminder(existing, reminder));
            result.Add(new Reminder
            {
                Id = match?.Id ?? (reminder.Id == Guid.Empty ? Guid.NewGuid() : reminder.Id),
                CalendarItemId = calendarItemId,
                DurationInSeconds = reminder.DurationInSeconds,
                ReminderType = reminder.ReminderType
            });
        }

        return HaveSameReminders(stored, result) ? null : result;
    }

    public static bool SameReminder(Reminder left, Reminder right)
        => left.DurationInSeconds == right.DurationInSeconds && left.ReminderType == right.ReminderType;

    public static bool HaveSameReminders(IReadOnlyCollection<Reminder> left, IReadOnlyCollection<Reminder> right)
    {
        static IEnumerable<(long, CalendarItemReminderType)> Keys(IEnumerable<Reminder> reminders)
            => reminders.Select(reminder => (reminder.DurationInSeconds, reminder.ReminderType)).Distinct().OrderBy(key => key);

        return Keys(left).SequenceEqual(Keys(right));
    }
}

using System;
using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Entities.Calendar;

[Preserve]
public class AccountCalendar : IAccountCalendar
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string RemoteCalendarId { get; set; }
    public string SynchronizationDeltaToken { get; set; }
    public string Name { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsSynchronizationEnabled { get; set; } = true;
    public bool IsExtended { get; set; } = true;
    public CalendarItemShowAs DefaultShowAs { get; set; } = CalendarItemShowAs.Busy;

    /// <summary>
    /// Seconds before the start of the reminder new events in this calendar get: null uses the app's
    /// default, <see cref="NoDefaultReminder"/> means none. Kept only in Wino.
    /// </summary>
    public long? DefaultReminderInSeconds { get; set; }

    public const long NoDefaultReminder = long.MinValue;

    /// <summary>The reminder a new event in this calendar gets, or null for none.</summary>
    public long? ResolveDefaultReminder(long appDefaultReminderInSeconds)
        => DefaultReminderInSeconds switch
        {
            NoDefaultReminder => null,
            { } seconds => seconds,
            null => appDefaultReminderInSeconds > 0 ? appDefaultReminderInSeconds : null
        };

    /// <summary>
    /// Unused for now.
    /// </summary>
    public string TextColorHex { get; set; }
    public string BackgroundColorHex { get; set; }
    public bool IsBackgroundColorUserOverridden { get; set; }
    public string TimeZone { get; set; }

    [Ignore]
    public MailAccount MailAccount { get; set; }
}

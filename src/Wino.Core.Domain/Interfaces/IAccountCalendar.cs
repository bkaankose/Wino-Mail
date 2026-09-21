using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IAccountCalendar
{
    string Name { get; set; }
    string TextColorHex { get; set; }
    string BackgroundColorHex { get; set; }
    bool IsPrimary { get; set; }
    bool IsReadOnly { get; set; }
    bool IsSynchronizationEnabled { get; set; }
    Guid AccountId { get; set; }
    string RemoteCalendarId { get; set; }
    bool IsExtended { get; set; }
    CalendarItemShowAs DefaultShowAs { get; set; }
    Guid Id { get; set; }
    MailAccount MailAccount { get; set; }

    /// <summary>
    /// Whether this is a pinned Exchange public calendar folder: transient, read-only, and with events that
    /// exist only in memory, so nothing about it or its events may be looked up in or written to the store.
    /// </summary>
    bool IsPublicFolder => false;
}

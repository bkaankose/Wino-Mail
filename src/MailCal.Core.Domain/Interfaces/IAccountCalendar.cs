using System;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

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
}

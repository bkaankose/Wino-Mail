using System;
using Itenso.TimePeriod;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface ICalendarItem
{
    string Title { get; }
    Guid Id { get; }
    IAccountCalendar AssignedCalendar { get; }
    DateTime StartDateTime { get; set; }
    DateTime EndDateTime { get; }
    ITimePeriod Period { get; }
    CalendarItemType ItemType { get; }
}

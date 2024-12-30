using System;
using Itenso.TimePeriod;

namespace Wino.Core.Domain.Interfaces
{
    public interface ICalendarItem
    {
        string Title { get; }
        Guid Id { get; }
        IAccountCalendar AssignedCalendar { get; }
        DateTime StartDate { get; set; }
        DateTime EndDate { get; }
        double DurationInSeconds { get; set; }
        ITimePeriod Period { get; }
    }
}

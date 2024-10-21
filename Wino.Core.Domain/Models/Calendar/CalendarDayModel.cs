using System;

namespace Wino.Core.Domain.Models.Calendar
{
    /// <summary>
    /// Represents a day in the calendar.
    /// Can hold events, appointments, wheather status etc.
    /// </summary>
    /// <param name="RepresentingDate">1 day that calendar column represents.</param>
    public record CalendarDayModel(DateTime RepresentingDate);
}

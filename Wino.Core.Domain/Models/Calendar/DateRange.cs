using System;

namespace Wino.Core.Domain.Models.Calendar
{
    /// <summary>
    /// Represents a range of dates.
    /// </summary>
    /// <param name="StartDate">Start date</param>
    /// <param name="EndDate">End date</param>
    public record DateRange(DateTime StartDate, DateTime EndDate);
}

using System;
using System.Linq;

namespace Wino.Core.Domain.Models.Calendar;

public class DateRange
{
    public DateRange(DateTime startDate, DateTime endDate)
    {
        StartDate = startDate;
        EndDate = endDate;
    }

    public DateTime StartDate { get; }
    public DateTime EndDate { get; }

    public int TotalDays => (EndDate - StartDate).Days;

    public override string ToString() => $"{StartDate.ToString("dd MMMM")} - {EndDate.ToString("dd MMMM")}";

    public bool IsInRange(DateTime date)
    {
        return date >= StartDate && date <= EndDate;
    }

    /// <summary>
    /// Gets the most visible month index in the visible date range.
    /// </summary>
    public int GetMostVisibleMonthIndex()
    {
        var dateRange = Enumerable.Range(0, (EndDate - StartDate).Days + 1).Select(offset => StartDate.AddDays(offset));

        var groupedByMonth = dateRange.GroupBy(date => date.Month)
                                 .Select(g => new { Month = g.Key, DayCount = g.Count() });

        // Find the month with the maximum count of days
        var mostVisibleMonth = groupedByMonth.OrderByDescending(g => g.DayCount).FirstOrDefault();

        return mostVisibleMonth?.Month ?? -1;
    }
}

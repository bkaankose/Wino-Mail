using System;

namespace Wino.Core.Domain.Models.Calendar
{
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

        public override string ToString()
        {
            return $"{StartDate.ToString("dd MMMM")} - {EndDate.ToString("dd MMMM")}";
        }

        public bool IsInRange(DateTime date)
        {
            return date >= StartDate && date <= EndDate;
        }
    }
}

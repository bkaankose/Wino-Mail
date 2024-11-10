using System;
using Itenso.TimePeriod;

namespace Wino.Core.Domain.Interfaces
{
    public interface ICalendarItem
    {
        string Name { get; }
        Guid Id { get; }
        DateTime StartTime { get; }
        DateTime EndTime { get; }
        TimeRange Period { get; }
    }
}

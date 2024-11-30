using System;
using Itenso.TimePeriod;

namespace Wino.Core.Domain.Interfaces
{
    public interface ICalendarItem
    {
        string Title { get; }
        Guid Id { get; }
        DateTimeOffset StartTime { get; }
        DateTimeOffset EndTime { get; }
        TimeRange Period { get; }
    }
}

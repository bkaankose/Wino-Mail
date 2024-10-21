using System;

namespace Wino.Core.Domain.Interfaces
{
    public interface ICalendarItem
    {
        Guid Id { get; }
        DateTime StartTime { get; }
        DateTime EndTime { get; }
    }
}

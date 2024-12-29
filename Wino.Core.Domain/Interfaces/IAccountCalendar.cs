using System;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAccountCalendar
    {
        string Name { get; set; }
        string TextColorHex { get; set; }
        string BackgroundColorHex { get; set; }
        bool IsPrimary { get; set; }
        Guid AccountId { get; set; }
        string RemoteCalendarId { get; set; }
        bool IsExtended { get; set; }
    }
}

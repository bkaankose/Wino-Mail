using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalDavCalendar
{
    public string RemoteCalendarId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string CTag { get; init; } = string.Empty;
    public string SyncToken { get; init; } = string.Empty;
    public string TimeZone { get; init; } = string.Empty;
    public string BackgroundColorHex { get; init; } = string.Empty;
    public bool IsReadOnly { get; init; }
    public bool SupportsEvents { get; init; } = true;
    public CalendarItemShowAs DefaultShowAs { get; init; } = CalendarItemShowAs.Busy;
    public double? Order { get; init; }
}


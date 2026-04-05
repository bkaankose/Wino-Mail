namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalDavCalendar
{
    public string RemoteCalendarId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string CTag { get; init; } = string.Empty;
    public string SyncToken { get; init; } = string.Empty;
}


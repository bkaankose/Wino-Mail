namespace Wino.Core.Domain.Enums;

public enum CalendarEventDisplayMode
{
    Stacked = 0,
    // Keep this persisted name and value for existing Cascade selections.
    Overlapped = 1,
    LimitedOverlap = 2,
    ProtectTitles = 3
}

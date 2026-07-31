using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain;

public static class CalendarItemActionOptions
{
    public static IReadOnlyList<CalendarItemShowAs> ShowAsOptions { get; } =
    [
        CalendarItemShowAs.Free,
        CalendarItemShowAs.Tentative,
        CalendarItemShowAs.Busy,
        CalendarItemShowAs.OutOfOffice,
        CalendarItemShowAs.WorkingElsewhere
    ];

    public static IReadOnlyList<CalendarItemStatus> ResponseOptions { get; } =
    [
        CalendarItemStatus.Accepted,
        CalendarItemStatus.Tentative,
        CalendarItemStatus.Cancelled
    ];
}

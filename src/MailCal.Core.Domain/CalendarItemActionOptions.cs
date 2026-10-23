using System.Collections.Generic;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain;

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

using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Calendar;

public sealed record CalendarContextMenuAction(
    CalendarContextMenuActionType ActionType,
    CalendarEventTargetType? TargetType = null,
    CalendarItemShowAs? ShowAs = null,
    CalendarItemStatus? ResponseStatus = null);

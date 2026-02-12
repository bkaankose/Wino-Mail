using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.Client.Calendar;

public record CalendarItemAdded(CalendarItem CalendarItem);
public record CalendarItemUpdated(CalendarItem CalendarItem, CalendarItemUpdateSource Source);
public record CalendarItemDeleted(CalendarItem CalendarItem);

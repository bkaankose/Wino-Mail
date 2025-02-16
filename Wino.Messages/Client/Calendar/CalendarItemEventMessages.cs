using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Messaging.Client.Calendar;

public record CalendarItemAdded(CalendarItem CalendarItem);
public record CalendarItemUpdated(CalendarItem CalendarItem);
public record CalendarItemDeleted(CalendarItem CalendarItem);

using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.Client.Calendar;

public record CalendarItemAdded(CalendarItem CalendarItem, EntityUpdateSource Source = EntityUpdateSource.Server);
public record CalendarItemUpdated(CalendarItem CalendarItem, EntityUpdateSource Source);
public record CalendarItemDeleted(CalendarItem CalendarItem, EntityUpdateSource Source = EntityUpdateSource.Server);

using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Client.Calendar;

public record CalendarItemAdded(CalendarItem CalendarItem, EntityUpdateSource Source = EntityUpdateSource.Server) : IUIMessage;
public record CalendarItemUpdated(CalendarItem CalendarItem, EntityUpdateSource Source) : IUIMessage;
public record CalendarItemDeleted(CalendarItem CalendarItem, EntityUpdateSource Source = EntityUpdateSource.Server) : IUIMessage;

using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Client.Calendar;

public record CalendarListRefreshed(List<AccountCalendar> AccountCalendars);
public record CalendarListAdded(AccountCalendar AccountCalendar) : IUIMessage;
public record CalendarListUpdated(AccountCalendar AccountCalendar) : IUIMessage;
public record CalendarListDeleted(AccountCalendar AccountCalendar) : IUIMessage;

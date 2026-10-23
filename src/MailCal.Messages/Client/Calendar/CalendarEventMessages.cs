using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Calendar;

namespace MailCal.Messaging.Client.Calendar;

public record CalendarListRefreshed(List<AccountCalendar> AccountCalendars);
public record CalendarListAdded(AccountCalendar AccountCalendar);
public record CalendarListUpdated(AccountCalendar AccountCalendar);
public record CalendarListDeleted(AccountCalendar AccountCalendar);

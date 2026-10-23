using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Core.Domain.Models.Calendar;

public class CalendarPickerAccountGroup
{
    public MailAccount Account { get; set; } = null!;
    public List<AccountCalendar> Calendars { get; set; } = [];
}

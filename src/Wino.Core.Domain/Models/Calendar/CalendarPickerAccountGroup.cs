using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Calendar;

public class CalendarPickerAccountGroup
{
    public MailAccount Account { get; set; } = null!;
    public List<AccountCalendar> Calendars { get; set; } = [];
}

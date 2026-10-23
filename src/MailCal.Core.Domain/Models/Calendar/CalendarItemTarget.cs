using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Calendar;

public record CalendarItemTarget(CalendarItem Item, CalendarEventTargetType TargetType);

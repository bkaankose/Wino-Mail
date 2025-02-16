using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public record CalendarItemTarget(CalendarItem Item, CalendarEventTargetType TargetType);

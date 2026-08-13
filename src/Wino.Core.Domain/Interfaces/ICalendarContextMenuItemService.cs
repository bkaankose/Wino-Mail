using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

public interface ICalendarContextMenuItemService
{
    IReadOnlyList<CalendarContextMenuItem> GetContextMenuItems(CalendarItem calendarItem);
}

using System.Linq;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Collections;

namespace Wino.Calendar.Helpers
{
    public static class CalendarXamlHelpers
    {
        public static CalendarItemViewModel GetFirstAllDayEvent(CalendarEventCollection collection)
            => (CalendarItemViewModel)collection.AllDayEvents.FirstOrDefault();
    }
}

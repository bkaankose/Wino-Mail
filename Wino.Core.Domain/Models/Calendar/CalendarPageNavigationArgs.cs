using System;

namespace Wino.Core.Domain.Models.Calendar
{
    public class CalendarPageNavigationArgs
    {
        /// <summary>
        /// When the app launches, automatically request the default calendar navigation options.
        /// </summary>
        public bool RequestDefaultNavigation { get; set; }

        /// <summary>
        /// Display the calendar view for the specified date.
        /// </summary>
        public DateTime NavigationDate { get; set; }
    }
}

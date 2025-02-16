using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization
{
    public class CalendarSynchronizationOptions
    {
        /// <summary>
        /// Unique id of synchronization.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// Account to execute synchronization for.
        /// </summary>
        public Guid AccountId { get; set; }

        /// <summary>
        /// Type of the synchronization to be performed.
        /// </summary>
        public CalendarSynchronizationType Type { get; set; }

        /// <summary>
        /// Calendar ids to synchronize.
        /// </summary>
        public List<Guid> SynchronizationCalendarIds { get; set; }

        public override string ToString() => $"Type: {Type}, Calendars: {(SynchronizationCalendarIds == null ? "All" : string.Join(",", SynchronizationCalendarIds))}";
    }
}

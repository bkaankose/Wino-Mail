using System;
using SQLite;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Entities.Calendar
{
    public class AccountCalendar : IAccountCalendar
    {
        [PrimaryKey]
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public string RemoteCalendarId { get; set; }
        public string SynchronizationDeltaToken { get; set; }
        public string Name { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsExtended { get; set; } = true;

        /// <summary>
        /// Unused for now.
        /// </summary>
        public string TextColorHex { get; set; }
        public string BackgroundColorHex { get; set; }
        public string TimeZone { get; set; }
    }
}

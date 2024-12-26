using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Calendar
{
    public class AccountCalendar
    {
        [PrimaryKey]
        public Guid Id { get; set; }
        public string RemoteCalendarId { get; set; }
        public string SynchronizationDeltaToken { get; set; }
        public Guid AccountId { get; set; }
        public string Name { get; set; }
        public string ColorHex { get; set; }
        public string TimeZone { get; set; }
        public bool IsPrimary { get; set; }
    }
}

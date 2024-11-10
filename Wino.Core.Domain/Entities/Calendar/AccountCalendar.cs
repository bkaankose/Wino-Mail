using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Calendar
{
    public class AccountCalendar
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        public Guid AccountId { get; set; }
        public string Name { get; set; }
        public string ColorHex { get; set; }
        public string TimeZoneId { get; set; }
    }
}

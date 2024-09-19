using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Calendar
{
    public class Attendee
    {
        [PrimaryKey]
        public Guid Id { get; set; }
        public Guid EventId { get; set; }
        public Guid ContactId { get; set; }
        public AttendeeStatus Status { get; set; }
        public bool IsOrganizer { get; set; }
    }
}

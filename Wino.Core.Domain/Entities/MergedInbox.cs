using System;
using SQLite;

namespace Wino.Core.Domain.Entities
{
    public class MergedInbox
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        public string Name { get; set; }
    }
}

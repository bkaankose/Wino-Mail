using System;
using SQLite;

namespace Wino.Core.Domain.Entities
{
    public class MailAccountAlias
    {
        [PrimaryKey]
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public string AliasAddress { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsVerified { get; set; }
    }
}

using System;
using SQLite;

namespace Wino.Core.Domain.Entities
{
    public class AccountSignature
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        public string HtmlBody { get; set; }
    }
}

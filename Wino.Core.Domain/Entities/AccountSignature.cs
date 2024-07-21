using System;
using SQLite;

namespace Wino.Domain.Entities
{
    public class AccountSignature
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string HtmlBody { get; set; }

        public Guid MailAccountId { get; set; }
    }
}

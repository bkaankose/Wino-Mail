using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail
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

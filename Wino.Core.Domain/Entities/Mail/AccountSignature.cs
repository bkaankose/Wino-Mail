using System;
using System.ComponentModel.DataAnnotations;

namespace Wino.Core.Domain.Entities.Mail;

public class AccountSignature
{
    [Key]
    public Guid Id { get; set; }

    public string Name { get; set; }

    public string HtmlBody { get; set; }

    public Guid MailAccountId { get; set; }
}

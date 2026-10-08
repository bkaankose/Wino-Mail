using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

public class MailCategoryAssignment
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid MailCategoryId { get; set; }

    public Guid MailCopyUniqueId { get; set; }
}

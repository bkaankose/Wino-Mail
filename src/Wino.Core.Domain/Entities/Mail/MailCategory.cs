using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Mail;

public class MailCategory
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid MailAccountId { get; set; }

    public string RemoteId { get; set; }

    public string Name { get; set; }

    public bool IsFavorite { get; set; }

    public string BackgroundColorHex { get; set; }

    public string TextColorHex { get; set; }

    public MailCategorySource Source { get; set; } = MailCategorySource.Local;
}

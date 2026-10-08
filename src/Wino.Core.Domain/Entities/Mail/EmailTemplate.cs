using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

public class EmailTemplate
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string HtmlContent { get; set; } = string.Empty;
}

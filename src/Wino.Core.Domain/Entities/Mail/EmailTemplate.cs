using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

public class EmailTemplate
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Fills an empty draft subject when the template is applied. Rows from before the column existed read null.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Comma- or semicolon-separated addresses filled into To when the draft has none yet.
    /// Rows from before the column existed read null.
    /// </summary>
    public string ToRecipients { get; set; } = string.Empty;

    /// <summary>
    /// Optional signature applied when the template is inserted, if it still belongs to the composing account.
    /// Rows from before the column existed read null.
    /// </summary>
    public Guid? SignatureId { get; set; }

    public string HtmlContent { get; set; } = string.Empty;
}

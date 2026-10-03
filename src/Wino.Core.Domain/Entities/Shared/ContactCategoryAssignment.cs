using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// A category applied to a contact. Categories are defined once per account and are the
/// same ones mails carry; see <see cref="Mail.MailCategory"/>.
/// </summary>
public class ContactCategoryAssignment
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid MailCategoryId { get; set; }
    [Indexed] public Guid ContactId { get; set; }
}

using System;
using SQLite;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Entities.Shared;

public class ContactPhoneNumber
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid ContactId { get; set; }
    public string Number { get; set; }
    public ContactPhoneKind Kind { get; set; }
    public int Order { get; set; }
    public bool IsPrimary { get; set; }
}

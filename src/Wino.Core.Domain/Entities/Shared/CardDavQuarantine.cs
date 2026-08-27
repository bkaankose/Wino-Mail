using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public sealed class CardDavQuarantine
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid AddressBookId { get; set; }
    public string ExactHref { get; set; }
    public string ETag { get; set; }
    public string PayloadReference { get; set; }
    public string ErrorCategory { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
}

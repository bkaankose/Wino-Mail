using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public sealed class CardDavOutboxItem
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid AccountId { get; set; }
    [Indexed] public Guid? ContactId { get; set; }
    [Indexed] public Guid? AddressBookId { get; set; }
    public Guid? DestinationAddressBookId { get; set; }
    public CardDavOutboxOperation Operation { get; set; }
    public CardDavOutboxState State { get; set; }
    public string IntendedHref { get; set; }
    public string BaseETag { get; set; }
    public string BasePayloadReference { get; set; }
    public Guid? DependsOnOperationId { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public string LastErrorCode { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
}

using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public sealed class CardDavConflict
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid AccountId { get; set; }
    [Indexed] public Guid ContactId { get; set; }
    [Indexed] public Guid AddressBookId { get; set; }
    public Guid? OutboxItemId { get; set; }
    public Guid? ResourceShadowId { get; set; }
    public CardDavConflictKind Kind { get; set; }
    public CardDavConflictResolution Resolution { get; set; }
    public string RemotePayloadReference { get; set; }
    public string RemoteETag { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
}

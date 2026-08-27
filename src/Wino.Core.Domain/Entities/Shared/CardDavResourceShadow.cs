using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public sealed class CardDavResourceShadow
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid AddressBookId { get; set; }
    [Indexed] public Guid? ContactId { get; set; }
    public string ExactHref { get; set; }
    public string ETag { get; set; }
    public string Uid { get; set; }
    public string VCardVersion { get; set; }
    public string PayloadReference { get; set; }
    public string RawHash { get; set; }
    public string SemanticHash { get; set; }
    public string DomainHash { get; set; }
    public long LastSeenGeneration { get; set; }
    public CardDavResourceStatus Status { get; set; }
}

using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>Retains EAS properties absent from domain models so mutations can round-trip them.</summary>
public sealed class EasItemShadow
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;
    [Indexed]
    public Guid AccountId { get; set; }
    [Indexed]
    public Guid CollectionId { get; set; }
    public string RemoteId { get; set; } = string.Empty;
    public string ApplicationData { get; set; } = string.Empty;
    public string Generation { get; set; } = string.Empty;
}

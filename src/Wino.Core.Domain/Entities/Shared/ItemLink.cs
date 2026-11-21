using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// A local link between a mail, an event, a task or a contact. Keys are the stable identities from
/// <see cref="Models.Links.ItemLinkKeys"/>, not row ids, because synchronization re-creates rows.
/// The titles are kept so a side that is gone or not loaded can still be named.
/// </summary>
public class ItemLink
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    public ItemLinkKind SourceKind { get; set; }
    public Guid SourceAccountId { get; set; }

    [Indexed]
    public string SourceKey { get; set; } = string.Empty;

    public ItemLinkKind TargetKind { get; set; }
    public Guid TargetAccountId { get; set; }

    [Indexed]
    public string TargetKey { get; set; } = string.Empty;

    public ItemLinkRelation Relation { get; set; }
    public string SourceTitle { get; set; } = string.Empty;
    public string SourceSubtitle { get; set; } = string.Empty;
    public string TargetTitle { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

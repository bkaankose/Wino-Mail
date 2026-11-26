using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// An Exchange ActiveSync collection that is not a mail folder (calendar, contacts, tasks, notes, journal).
/// Mail collections are MailItemFolder rows; these keep their hierarchy and sync state here.
/// </summary>
public sealed class EasCollectionState
{
    [PrimaryKey]
    public Guid Id { get; set; }
    [Indexed]
    public Guid AccountId { get; set; }
    public string ServerId { get; set; } = string.Empty;
    public string ParentServerId { get; set; } = string.Empty;
    public int Type { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string SyncKey { get; set; } = "0";
    public string Generation { get; set; } = string.Empty;

    /// <summary>The calendar, address book or task list this collection backs, when one was created.</summary>
    public Guid? EntityId { get; set; }
}

using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// An Exchange ActiveSync Sync request with commands that was sent for a collection and not answered yet. It is kept
/// until the server's answer is stored, so a request whose response was lost is sent again unchanged, with the same
/// synchronization key, before anything else goes to that collection.
/// </summary>
public sealed class EasPendingSync
{
    /// <summary>The collection: a MailItemFolder id or an EasCollectionState id.</summary>
    [PrimaryKey]
    public Guid CollectionId { get; set; }
    [Indexed]
    public Guid AccountId { get; set; }
    public string SyncKey { get; set; } = string.Empty;

    /// <summary>The Commands element sent, as XML.</summary>
    public string Commands { get; set; } = string.Empty;
    public bool GetChanges { get; set; }
    public bool DeletesAsMoves { get; set; }
    public int WindowSize { get; set; }

    /// <summary>What each command does to the stored copy once the server accepts it, as XML; empty when nothing.</summary>
    public string Effects { get; set; } = string.Empty;
}

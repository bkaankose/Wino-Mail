using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public sealed class CardDavAddressBookState
{
    [PrimaryKey] public Guid AddressBookId { get; set; }
    [Indexed] public Guid AccountId { get; set; }
    public string ExactHref { get; set; }

    /// <summary>Token of the last change set applied locally. Null until a full listing completed.</summary>
    public string SyncToken { get; set; }

    /// <summary>Collection tag the server reported when the last synchronization started.</summary>
    public string CollectionTag { get; set; }
    public bool SupportsSyncCollection { get; set; }
    public bool SupportsMultiget { get; set; }
    public bool SupportsVCard4 { get; set; }
    public bool IsReadOnly { get; set; }
    public DateTime? LastSyncUtc { get; set; }
}

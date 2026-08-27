using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public sealed class CardDavAddressBookState
{
    [PrimaryKey] public Guid AddressBookId { get; set; }
    [Indexed] public Guid AccountId { get; set; }
    public string ExactHref { get; set; }
    public string SyncToken { get; set; }
    public string CollectionTag { get; set; }
    public bool SupportsSyncCollection { get; set; }
    public bool SupportsMultiget { get; set; }
    public bool SupportsAddressBookQuery { get; set; }
    public bool SupportsInlineAddressData { get; set; }
    public bool SupportsVCard3 { get; set; } = true;
    public bool SupportsVCard4 { get; set; }
    public bool SupportsExtendedMkCol { get; set; }
    public bool SupportsAddMember { get; set; }
    public bool SupportsPreferMinimal { get; set; }
    public long? MaximumResourceSize { get; set; }
    public bool IsReadOnly { get; set; }
    public int LearnedMultigetBatchSize { get; set; } = 100;
    public string Quirks { get; set; }
    public long ReconciliationGeneration { get; set; }
    public bool RequiresFullReconciliation { get; set; }
    public bool IsUnavailable { get; set; }
    public DateTime? LastFullSyncUtc { get; set; }
    public DateTime? LastIncrementalSyncUtc { get; set; }
}

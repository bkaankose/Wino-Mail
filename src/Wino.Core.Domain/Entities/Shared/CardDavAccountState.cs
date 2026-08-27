using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public sealed class CardDavAccountState
{
    [PrimaryKey] public Guid AccountId { get; set; }
    public string ContextHref { get; set; }
    public string PrincipalHref { get; set; }
    public string AddressBookHomeHref { get; set; }
    public bool SupportsAddressBookCreation { get; set; }
    public DateTime? DiscoveryExpiresUtc { get; set; }
    public DateTime? CapabilitiesExpireUtc { get; set; }
    public DateTime? BackoffUntilUtc { get; set; }
    public bool RequiresRediscovery { get; set; }
}

#nullable enable
namespace Wino.Core.Domain.Models.Accounts;

public enum MicrosoftNamespaceType
{
    Unknown,
    Managed,
    Federated
}

/// <summary>The answer of Microsoft's user-realm endpoint for one address.</summary>
public sealed record MicrosoftUserRealm(MicrosoftNamespaceType NamespaceType, string? DomainName = null, string? FederationBrandName = null)
{
    public static MicrosoftUserRealm Unknown { get; } = new(MicrosoftNamespaceType.Unknown);

    public bool IsMicrosoft365 => NamespaceType is MicrosoftNamespaceType.Managed or MicrosoftNamespaceType.Federated;
}

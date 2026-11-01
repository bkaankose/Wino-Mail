#nullable enable
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>What an Exchange endpoint's authentication challenge revealed.</summary>
public sealed class ExchangeAuthProbeResult
{
    public ExchangeAuthCapability Capability { get; init; }

    /// <summary>The OAuth authority derived from <see cref="AuthorizationUri"/>, for example https://adfs.example.com/adfs.</summary>
    public string? Authority { get; init; }

    /// <summary>The authorization_uri parameter of the Bearer challenge.</summary>
    public string? AuthorizationUri { get; init; }

    /// <summary>The issuer_kind parameter of the Bearer challenge, when the server sends one.</summary>
    public string? IssuerKind { get; init; }

    public static ExchangeAuthProbeResult Unknown { get; } = new() { Capability = ExchangeAuthCapability.Unknown };
}

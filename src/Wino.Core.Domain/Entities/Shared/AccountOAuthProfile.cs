#nullable enable
using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>The non-secret OAuth registration an account signs in with. Tokens never live here.</summary>
public class AccountOAuthProfile
{
    [PrimaryKey]
    public Guid AccountId { get; set; }

    public string Issuer { get; set; } = string.Empty;
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? RevocationEndpoint { get; set; }
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Space-separated, as sent in the scope parameter.</summary>
    public string Scopes { get; set; } = string.Empty;

    public string? Resource { get; set; }
    public OAuthRedirectKind RedirectKind { get; set; }
    public int? LoopbackPort { get; set; }
    public OAuthTrustSource TrustSource { get; set; }
    public OAuthSaslMechanism SaslMechanism { get; set; }

    public AccountOAuthProfile Clone() => (AccountOAuthProfile)MemberwiseClone();
}

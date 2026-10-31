using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.CardDav;

public sealed class DavAuthenticationProfile
{
    public DavAuthenticationKind Kind { get; init; } = DavAuthenticationKind.Basic;
    public string Username { get; init; }
    public string Password { get; init; }

    /// <summary>Replaced by the transport after <see cref="RefreshBearerTokenAsync"/> returns a new token.</summary>
    public string BearerToken { get; set; }

    /// <summary>The Authorization scheme the token is sent with. Some providers expect their own instead of Bearer.</summary>
    public string BearerScheme { get; init; } = "Bearer";

    /// <summary>
    /// Optional. Called once with the rejected token when a Bearer request gets 401; returns the token to retry with.
    /// </summary>
    public Func<string, CancellationToken, Task<string>> RefreshBearerTokenAsync { get; init; }
}

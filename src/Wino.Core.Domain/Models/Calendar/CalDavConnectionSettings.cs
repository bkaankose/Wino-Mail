using System;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalDavConnectionSettings
{
    public Uri ServiceUri { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    /// <summary>Bearer authentication for OAuth accounts. When null, Basic with <see cref="Username"/> and <see cref="Password"/>.</summary>
    public DavAuthenticationProfile? Authentication { get; init; }
}

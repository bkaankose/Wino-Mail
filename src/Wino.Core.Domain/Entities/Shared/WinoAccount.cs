using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public class WinoAccount
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string AccountStatus { get; set; } = string.Empty;

    public bool HasPassword { get; set; }

    public bool HasGoogleLogin { get; set; }

    public bool HasFacebookLogin { get; set; }

    public string AccessToken { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresAtUtc { get; set; }

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime RefreshTokenExpiresAtUtc { get; set; }

    public DateTime LastAuthenticatedUtc { get; set; }
}

#nullable enable
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authorization;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// What the address-first setup page already knows when the user opens Advanced setup: the address, the servers it
/// found, the credential that was typed and an OAuth session. Advanced setup takes ownership of the session.
/// </summary>
public sealed class ImapSetupPrefill
{
    public string EmailAddress { get; init; } = string.Empty;
    public string SenderName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public SpecialImapProvider SpecialImapProvider { get; init; }
    public string? LoginAddress { get; init; }
    public CustomServerInformation? ServerInformation { get; init; }
    public string? CalDavServiceUrl { get; init; }
    public string? CardDavServiceUrl { get; init; }
    public OAuthSetupSession? OAuthSession { get; init; }

    /// <summary>Opens on the Servers step instead of the sign-in step.</summary>
    public bool StartOnServers { get; init; } = true;
}

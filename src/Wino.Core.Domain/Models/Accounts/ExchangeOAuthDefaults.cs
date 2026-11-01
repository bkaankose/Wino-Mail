#nullable enable
using System;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// The OAuth settings a modern-auth Exchange account starts with when the server only names its authority. They
/// follow the convention Exchange publishes Outlook on the web under: Exchange's own application id as the client,
/// the server origin as the resource, and the OWA address as the reply URL, which ADFS and Hybrid Modern Auth
/// deployments register for that client. The embedded browser captures the reply before it loads, so the page at
/// that address is never shown.
/// </summary>
public static class ExchangeOAuthDefaults
{
    /// <summary>The well-known application id of Exchange Server (Office 365 Exchange Online in Microsoft Entra ID).</summary>
    public const string ClientId = "00000002-0000-0ff1-ce00-000000000000";

    /// <summary>https://mail.example.com/ for https://mail.example.com/EWS/Exchange.asmx; null for anything but HTTPS.</summary>
    public static string? GetResource(string? ewsUrl)
        => GetHttpsOrigin(ewsUrl) is { } origin ? origin + "/" : null;

    /// <summary>https://mail.example.com/owa/ for https://mail.example.com/EWS/Exchange.asmx; null for anything but HTTPS.</summary>
    public static string? GetRedirectUri(string? ewsUrl)
        => GetHttpsOrigin(ewsUrl) is { } origin ? origin + "/owa/" : null;

    /// <summary>
    /// Switches <paramref name="serverInformation"/> to modern auth with <paramref name="authority"/>, filling the client
    /// id, resource and reply URL it does not set yet from its EWS URL.
    /// </summary>
    public static void Apply(CustomServerInformation serverInformation, string authority)
    {
        ArgumentNullException.ThrowIfNull(serverInformation);

        serverInformation.UseOAuthAuthentication = true;
        serverInformation.OAuthAuthority = authority.Trim();

        if (string.IsNullOrWhiteSpace(serverInformation.OAuthClientId))
            serverInformation.OAuthClientId = ClientId;

        if (string.IsNullOrWhiteSpace(serverInformation.OAuthResource))
            serverInformation.OAuthResource = GetResource(serverInformation.IncomingServer);

        if (string.IsNullOrWhiteSpace(serverInformation.OAuthRedirectUri))
            serverInformation.OAuthRedirectUri = GetRedirectUri(serverInformation.IncomingServer);
    }

    private static string? GetHttpsOrigin(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}

using System;
using System.Collections.Generic;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public class CustomServerInformation
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>
    /// This field is ignored. DisplayName is stored in MailAccount as SenderName from now.
    /// </summary>
    [Ignore]
    public string DisplayName { get; set; }
    public string Address { get; set; }
    public string IncomingServer { get; set; }
    public string IncomingServerUsername { get; set; }
    public string IncomingServerPassword { get; set; }
    public string IncomingServerPort { get; set; }

    public CustomIncomingServerType IncomingServerType { get; set; }

    public string OutgoingServer { get; set; }
    public string OutgoingServerPort { get; set; }
    public string OutgoingServerUsername { get; set; }
    public string OutgoingServerPassword { get; set; }

    public string CalDavServiceUrl { get; set; }
    public string CalDavUsername { get; set; }
    public string CalDavPassword { get; set; }
    public ImapCalendarSupportMode CalendarSupportMode { get; set; }

    /// <summary>
    /// useSSL True: SslOnConnect
    /// useSSL False: StartTlsWhenAvailable
    /// </summary>

    public ImapConnectionSecurity IncomingServerSocketOption { get; set; }
    public ImapAuthenticationMethod IncomingAuthenticationMethod { get; set; }


    public ImapConnectionSecurity OutgoingServerSocketOption { get; set; }
    public ImapAuthenticationMethod OutgoingAuthenticationMethod { get; set; }

    public string ProxyServer { get; set; }
    public string ProxyServerPort { get; set; }

    /// <summary>
    /// When true, this (Exchange/EWS) account authenticates with modern auth (OAuth2 bearer token)
    /// instead of Basic/NTLM <see cref="IncomingServerPassword"/>. The OAuth* fields below carry the
    /// per-account OIDC configuration and durable refresh token.
    /// </summary>
    public bool UseOAuthAuthentication { get; set; }

    /// <summary>OIDC authority base URL, e.g. <c>https://wsfed.mtec360.com/adfs</c>.</summary>
    public string OAuthAuthority { get; set; }

    /// <summary>OAuth client id used for the auth-code + PKCE flow.</summary>
    public string OAuthClientId { get; set; }

    /// <summary>Protected resource the access token is requested for, e.g. <c>https://mail.mtec360.com/</c>.</summary>
    public string OAuthResource { get; set; }

    /// <summary>Redirect URI registered with the issuer (exact-match).</summary>
    public string OAuthRedirectUri { get; set; }

    /// <summary>
    /// Durable refresh token. Stored alongside <see cref="IncomingServerPassword"/> and shares its
    /// at-rest posture (plaintext in the local, per-user packaged SQLite store). The ephemeral access
    /// token is never persisted — it is held in memory and re-derived from this refresh token.
    /// </summary>
    public string OAuthRefreshToken { get; set; }

    /// <summary>
    /// Number of concurrent clients that can connect to the server.
    /// Default is 5.
    /// </summary>
    public int MaxConcurrentClients { get; set; }

    public Dictionary<string, string> GetConnectionProperties()
    {
        // Printout the public connection properties.

        var connectionProperties = new Dictionary<string, string>
        {
            { "IncomingServer", IncomingServer },
            { "IncomingServerPort", IncomingServerPort },
            { "IncomingServerSocketOption", IncomingServerSocketOption.ToString() },
            { "IncomingAuthenticationMethod", IncomingAuthenticationMethod.ToString() },
            { "OutgoingServer", OutgoingServer },
            { "OutgoingServerPort", OutgoingServerPort },
            { "OutgoingServerSocketOption", OutgoingServerSocketOption.ToString() },
            { "OutgoingAuthenticationMethod", OutgoingAuthenticationMethod.ToString() },
            { "CalendarSupportMode", CalendarSupportMode.ToString() },
            { "CalDavServiceUrl", CalDavServiceUrl },
            { "ProxyServer", ProxyServer },
            { "ProxyServerPort", ProxyServerPort }
        };

        return connectionProperties;
    }
}

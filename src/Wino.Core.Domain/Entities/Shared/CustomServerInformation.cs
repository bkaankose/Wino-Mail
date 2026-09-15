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
    public string CardDavServiceUrl { get; set; }
    public ImapCalendarSupportMode CalendarSupportMode { get; set; }

    /// <summary>
    /// useSSL True: SslOnConnect
    /// useSSL False: StartTlsWhenAvailable
    /// </summary>

    public ImapConnectionSecurity IncomingServerSocketOption { get; set; }
    public ImapAuthenticationMethod IncomingAuthenticationMethod { get; set; }


    public ImapConnectionSecurity OutgoingServerSocketOption { get; set; }
    public ImapAuthenticationMethod OutgoingAuthenticationMethod { get; set; }

    public ImapConnectionPolicyVersion ConnectionPolicyVersion { get; set; }

    public string ProxyServer { get; set; }
    public string ProxyServerPort { get; set; }

    /// <summary>
    /// Number of concurrent clients that can connect to the server.
    /// Default is 5.
    /// </summary>
    public int MaxConcurrentClients { get; set; }

    /// <summary>True when an Exchange account uses OAuth (bearer tokens) instead of password auth.</summary>
    public bool UseOAuthAuthentication { get; set; }

    /// <summary>OIDC authority, e.g. https://adfs.example.com/adfs.</summary>
    public string OAuthAuthority { get; set; }

    public string OAuthClientId { get; set; }

    /// <summary>Protected resource the token is requested for, e.g. https://mail.example.com/.</summary>
    public string OAuthResource { get; set; }

    public string OAuthRedirectUri { get; set; }

    /// <summary>Long-lived refresh token; access tokens are minted from it and kept in memory only.</summary>
    public string OAuthRefreshToken { get; set; }

    /// <summary>The user's transport choice for an Exchange account; Automatic follows detection.</summary>
    public ExchangeTransport ExchangeTransport { get; set; }

    /// <summary>
    /// What Autodiscover said the mailbox offers, recorded by setup or by the MAPI synchronizer when it
    /// learns the protocol is not advertised. Automatic means not detected yet.
    /// </summary>
    public ExchangeTransport DetectedExchangeTransport { get; set; }

    /// <summary>
    /// The transport the synchronizer factory acts on: an explicit choice wins, detection decides under
    /// Automatic, and an undecided account tries MAPI/HTTP first so the MAPI path can fall back itself.
    /// </summary>
    [Ignore]
    public ExchangeTransport EffectiveExchangeTransport
        => ExchangeTransport != ExchangeTransport.Automatic ? ExchangeTransport
         : DetectedExchangeTransport == ExchangeTransport.Ews ? ExchangeTransport.Ews
         : ExchangeTransport.MapiHttp;

    [Ignore]
    public List<MailServerCertificateTrust> PendingCertificateTrusts { get; set; } = [];

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
            { "ProxyServerPort", ProxyServerPort },
            { "UseOAuthAuthentication", UseOAuthAuthentication.ToString() },
            { "ExchangeTransport", ExchangeTransport.ToString() },
            { "DetectedExchangeTransport", DetectedExchangeTransport.ToString() }
        };

        return connectionProperties;
    }
}

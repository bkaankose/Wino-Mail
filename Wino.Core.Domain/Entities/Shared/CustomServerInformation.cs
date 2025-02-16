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
            { "ProxyServer", ProxyServer },
            { "ProxyServerPort", ProxyServerPort }
        };

        return connectionProperties;
    }
}

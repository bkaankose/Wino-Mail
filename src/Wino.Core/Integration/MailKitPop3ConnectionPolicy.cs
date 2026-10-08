using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Pop3;
using MailKit.Net.Proxy;
using MailKit.Security;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Integration;

internal static class MailKitPop3ConnectionPolicy
{
    public static async Task ConnectAndAuthenticateAsync(
        Pop3Client client,
        CustomServerInformation serverInformation,
        IServerCertificateTrustService certificateTrustService = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(serverInformation);

        var host = serverInformation.IncomingServer;
        var port = int.Parse(serverInformation.IncomingServerPort);

        if (!string.IsNullOrWhiteSpace(serverInformation.ProxyServer))
        {
            client.ProxyClient = new HttpProxyClient(
                serverInformation.ProxyServer,
                int.Parse(serverInformation.ProxyServerPort));
        }

        var storedTrust = certificateTrustService == null
            ? null
            : await certificateTrustService
                .GetTrustAsync(serverInformation.AccountId, MailServerProtocol.Pop3, host, port)
                .ConfigureAwait(false);
        var transientTrust = serverInformation.PendingCertificateTrusts?
            .LastOrDefault(item => item.Protocol == MailServerProtocol.Pop3 &&
                                   string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase) &&
                                   item.Port == port);

        client.ServerCertificateValidationCallback = (_, certificate, chain, errors)
            => MailKitServerCertificateValidator.Validate(
                certificate, chain, errors, MailServerProtocol.Pop3, host, port, storedTrust, transientTrust);

        await client.ConnectAsync(host, port, GetSocketOptions(serverInformation), cancellationToken).ConfigureAwait(false);

        if (serverInformation.IncomingAuthenticationMethod == ImapAuthenticationMethod.None)
            return;

        var credentials = new NetworkCredential(
            serverInformation.IncomingServerUsername,
            serverInformation.IncomingServerPassword);

        var authentication = serverInformation.IncomingAuthenticationMethod;
        if (authentication is ImapAuthenticationMethod.Auto or ImapAuthenticationMethod.NormalPassword)
        {
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            client.AuthenticationMechanisms.Remove("OAUTHBEARER");
            await client.AuthenticateAsync(credentials, cancellationToken).ConfigureAwait(false);
            return;
        }

        var mechanism = authentication switch
        {
            ImapAuthenticationMethod.Ntlm => "NTLM",
            ImapAuthenticationMethod.CramMd5 => "CRAM-MD5",
            ImapAuthenticationMethod.DigestMd5 => "DIGEST-MD5",
            ImapAuthenticationMethod.EncryptedPassword => "LOGIN",
            _ => throw new NotSupportedException($"POP3 authentication method '{authentication}' is not supported.")
        };

        client.AuthenticationMechanisms.Clear();
        client.AuthenticationMechanisms.Add(mechanism);
        await client.AuthenticateAsync(SaslMechanism.Create(mechanism, credentials), cancellationToken).ConfigureAwait(false);
    }

    internal static SecureSocketOptions GetSocketOptions(CustomServerInformation serverInformation)
        => serverInformation.IncomingServerSocketOption switch
        {
            ImapConnectionSecurity.Auto => SecureSocketOptions.Auto,
            ImapConnectionSecurity.None => SecureSocketOptions.None,
            ImapConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
            ImapConnectionSecurity.SslTls => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.Auto
        };
}

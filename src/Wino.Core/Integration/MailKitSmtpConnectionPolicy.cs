using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Proxy;
using MailKit.Net.Smtp;
using MailKit.Security;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Integration;

internal static class MailKitSmtpConnectionPolicy
{
    public static async Task ConnectAndAuthenticateAsync(
        SmtpClient client,
        CustomServerInformation serverInformation,
        IServerCertificateTrustService certificateTrustService = null,
        CancellationToken cancellationToken = default,
        IOAuthTokenService oauthTokenService = null,
        Guid oauthTokenKey = default)
    {
        var host = serverInformation.OutgoingServer;
        var port = int.Parse(serverInformation.OutgoingServerPort);
        var corrected = UsesCorrectedPolicy(serverInformation);

        if (corrected && !string.IsNullOrWhiteSpace(serverInformation.ProxyServer))
        {
            client.ProxyClient = new HttpProxyClient(
                serverInformation.ProxyServer,
                int.Parse(serverInformation.ProxyServerPort));
        }

        var storedTrust = certificateTrustService == null
            ? null
            : await certificateTrustService
                .GetTrustAsync(serverInformation.AccountId, MailServerProtocol.Smtp, host, port)
                .ConfigureAwait(false);
        var transientTrust = serverInformation.PendingCertificateTrusts?
            .LastOrDefault(item => item.Protocol == MailServerProtocol.Smtp &&
                                   string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase) &&
                                   item.Port == port);

        client.ServerCertificateValidationCallback = (_, certificate, chain, errors)
            => MailKitServerCertificateValidator.Validate(
                certificate, chain, errors, MailServerProtocol.Smtp, host, port, storedTrust, transientTrust);

        await client.ConnectAsync(host, port, GetSocketOptions(serverInformation), cancellationToken).ConfigureAwait(false);

        var authentication = serverInformation.OutgoingAuthenticationMethod;
        if (authentication == ImapAuthenticationMethod.OAuth2)
        {
            await MailKitOAuthAuthenticator.AuthenticateAsync(
                client,
                serverInformation.OutgoingServerUsername,
                oauthTokenService,
                oauthTokenKey,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (corrected && authentication == ImapAuthenticationMethod.None)
            return;

        var credentials = new NetworkCredential(
            serverInformation.OutgoingServerUsername,
            serverInformation.OutgoingServerPassword);

        if (!corrected || authentication is ImapAuthenticationMethod.Auto or ImapAuthenticationMethod.NormalPassword)
        {
            if (corrected)
            {
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.AuthenticationMechanisms.Remove("OAUTHBEARER");
            }

            await client.AuthenticateAsync(credentials, cancellationToken).ConfigureAwait(false);
            return;
        }

        var mechanism = authentication switch
        {
            ImapAuthenticationMethod.Ntlm => "NTLM",
            ImapAuthenticationMethod.CramMd5 => "CRAM-MD5",
            ImapAuthenticationMethod.DigestMd5 => "DIGEST-MD5",
            ImapAuthenticationMethod.EncryptedPassword => "LOGIN",
            _ => throw new NotSupportedException($"SMTP authentication method '{authentication}' is not supported.")
        };

        client.AuthenticationMechanisms.Clear();
        client.AuthenticationMechanisms.Add(mechanism);
        await client.AuthenticateAsync(SaslMechanism.Create(mechanism, credentials), cancellationToken).ConfigureAwait(false);
    }

    // OAuth2 accounts always use the corrected policy so a bearer token is never sent without the configured TLS.
    // GetSocketOptions applies the same rule.
    private static bool UsesCorrectedPolicy(CustomServerInformation serverInformation)
        => serverInformation.ConnectionPolicyVersion == ImapConnectionPolicyVersion.Corrected ||
           serverInformation.OutgoingAuthenticationMethod == ImapAuthenticationMethod.OAuth2;

    internal static SecureSocketOptions GetSocketOptions(CustomServerInformation serverInformation)
    {
        if (serverInformation.ConnectionPolicyVersion == ImapConnectionPolicyVersion.Legacy &&
            serverInformation.OutgoingAuthenticationMethod != ImapAuthenticationMethod.OAuth2)
            return SecureSocketOptions.Auto;

        return serverInformation.OutgoingServerSocketOption switch
        {
            ImapConnectionSecurity.Auto => SecureSocketOptions.Auto,
            ImapConnectionSecurity.None => SecureSocketOptions.None,
            ImapConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
            ImapConnectionSecurity.SslTls => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.Auto
        };
    }
}

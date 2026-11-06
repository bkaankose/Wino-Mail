using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using MailKit.Net.Proxy;
using MailKit.Security;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Integration;

namespace Wino.Core.Services;

/// <summary>
/// Reads the IMAP server's advertised SASL mechanisms during setup. Every failure means "unknown",
/// so a slow or broken server never holds up the password path.
/// </summary>
public sealed class ImapAuthCapabilityProbe : IImapAuthCapabilityProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = Log.ForContext<ImapAuthCapabilityProbe>();
    private readonly IServerCertificateTrustService? _certificateTrustService;

    public ImapAuthCapabilityProbe(IServerCertificateTrustService? certificateTrustService = null)
    {
        _certificateTrustService = certificateTrustService;
    }

    public async Task<ImapAuthCapabilities> ProbeAsync(CustomServerInformation serverInformation, CancellationToken cancellationToken = default)
    {
        if (serverInformation == null ||
            string.IsNullOrWhiteSpace(serverInformation.IncomingServer) ||
            !int.TryParse(serverInformation.IncomingServerPort, out var port))
        {
            return ImapAuthCapabilities.Unknown;
        }

        var host = serverInformation.IncomingServer.Trim();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var storedTrust = _certificateTrustService == null || serverInformation.AccountId == Guid.Empty
                ? null
                : await _certificateTrustService
                    .GetTrustAsync(serverInformation.AccountId, MailServerProtocol.Imap, host, port)
                    .ConfigureAwait(false);
            var transientTrust = serverInformation.PendingCertificateTrusts?
                .LastOrDefault(item => item.Protocol == MailServerProtocol.Imap &&
                                       string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase) &&
                                       item.Port == port);

            using var client = new ImapClient
            {
                Timeout = (int)ProbeTimeout.TotalMilliseconds
            };

            if (!string.IsNullOrEmpty(serverInformation.ProxyServer) &&
                int.TryParse(serverInformation.ProxyServerPort, out var proxyPort))
            {
                client.ProxyClient = new HttpProxyClient(serverInformation.ProxyServer, proxyPort);
            }

            // A certificate accepted only through a user's trust override does not count as verified:
            // discovery must not follow an issuer named by a server whose identity was not proven.
            var certificateVerified = false;
            client.ServerCertificateValidationCallback = (_, certificate, chain, sslPolicyErrors) =>
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                {
                    certificateVerified = true;
                    return true;
                }

                return global::Wino.Services.ServerCertificateValidator.Validate(
                    certificate, chain, sslPolicyErrors, MailServerProtocol.Imap, host, port, storedTrust, transientTrust);
            };

            await client.ConnectAsync(host, port, GetSocketOptions(serverInformation), timeout.Token).ConfigureAwait(false);

            var mechanisms = new List<string>(client.AuthenticationMechanisms);
            var isTlsVerified = client.IsSecure && certificateVerified;

            try
            {
                await client.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.Debug(ex, "IMAP capability probe could not log out cleanly from {Host}:{Port}.", host, port);
            }

            return new ImapAuthCapabilities
            {
                Reached = true,
                IsTlsVerified = isTlsVerified,
                Mechanisms = mechanisms,
                SupportsOAuthBearer = mechanisms.Contains("OAUTHBEARER", StringComparer.OrdinalIgnoreCase),
                SupportsXOAuth2 = mechanisms.Contains("XOAUTH2", StringComparer.OrdinalIgnoreCase)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug("IMAP capability probe for {Host}:{Port} did not complete: {Reason}", host, port, ex.GetBaseException().Message);
            return ImapAuthCapabilities.Unknown;
        }
    }

    private static SecureSocketOptions GetSocketOptions(CustomServerInformation serverInformation) => serverInformation.IncomingServerSocketOption switch
    {
        ImapConnectionSecurity.Auto => SecureSocketOptions.Auto,
        ImapConnectionSecurity.None => SecureSocketOptions.None,
        ImapConnectionSecurity.StartTls => serverInformation.ConnectionPolicyVersion == ImapConnectionPolicyVersion.Corrected
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.StartTlsWhenAvailable,
        ImapConnectionSecurity.SslTls => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.None
    };
}

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Pop3;
using Wino.Core.Diagnostics;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Integration;

namespace Wino.Core.Services;

public sealed class Pop3TestService : IPop3TestService
{
    private readonly IServerCertificateTrustService _certificateTrustService;

    public Pop3TestService(IServerCertificateTrustService certificateTrustService = null)
    {
        _certificateTrustService = certificateTrustService;
    }

    public async Task<Pop3ConnectivityTestResult> TestConnectionAsync(
        CustomServerInformation serverInformation,
        CancellationToken cancellationToken = default)
    {
        using var protocolLogStream = new MemoryStream();

        try
        {
            using var protocolLogger = new WinoProtocolLogger(protocolLogStream, MailProtocol.Pop3);
            using var client = new Pop3Client(protocolLogger);
            await MailKitPop3ConnectionPolicy.ConnectAndAuthenticateAsync(
                client, serverInformation, _certificateTrustService, cancellationToken).ConfigureAwait(false);

            if (!client.SupportsUids)
                throw new NotSupportedException("This POP3 server does not support UIDL. Wino requires stable UIDL identifiers to prevent duplicate or incorrect messages.");

            await client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return Pop3ConnectivityTestResult.Success();
        }
        catch (MailServerCertificateException ex)
        {
            return new Pop3ConnectivityTestResult(false, false, ex.Message, null, ex.Failure);
        }
        catch (Exception ex)
        {
            var reason = ex.GetBaseException().Message;
            var wrapped = new Pop3ValidationException(
                string.IsNullOrWhiteSpace(reason) ? "POP3 server validation failed." : $"POP3 server validation failed: {reason}",
                Encoding.UTF8.GetString(protocolLogStream.ToArray()),
                ex);
            return Pop3ConnectivityTestResult.Failure(wrapped);
        }
    }
}

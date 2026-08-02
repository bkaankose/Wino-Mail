using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Diagnostics;
using Wino.Core.Integration;

namespace Wino.Core.Services;

public class ImapTestService : IImapTestService
{
    private readonly IServerCertificateTrustService _certificateTrustService;

    public ImapTestService(IServerCertificateTrustService certificateTrustService = null)
    {
        _certificateTrustService = certificateTrustService;
    }

    public async Task TestImapConnectionAsync(CustomServerInformation serverInformation)
    {
        using var protocolLogStream = new MemoryStream();
        var protocol = "IMAP";

        try
        {
            var poolOptions = ImapClientPoolOptions.CreateTestPool(
                serverInformation,
                () => new WinoProtocolLogger(protocolLogStream, MailProtocol.Imap),
                _certificateTrustService);

            using (var clientPool = new ImapClientPool(poolOptions))
            {
                // This call will make sure that everything is authenticated + connected successfully.
                var client = await clientPool.GetClientAsync();

                clientPool.Release(client);
            }

            // Test SMTP connectivity.
            protocol = "SMTP";
            using var smtpProtocolLogger = new WinoProtocolLogger(protocolLogStream, MailProtocol.Smtp);
            using var smtpClient = new SmtpClient(smtpProtocolLogger);
            if (!smtpClient.IsConnected)
                await MailKitSmtpConnectionPolicy.ConnectAndAuthenticateAsync(
                    smtpClient,
                    serverInformation,
                    _certificateTrustService).ConfigureAwait(false);

            await smtpClient.DisconnectAsync(true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var actualError = ex.GetBaseException().Message;
            var message = string.IsNullOrWhiteSpace(actualError)
                ? $"{protocol} server validation failed."
                : $"{protocol} server validation failed: {actualError}";

            throw new ImapValidationException(
                message,
                Encoding.UTF8.GetString(protocolLogStream.ToArray()),
                ex);
        }
    }
}

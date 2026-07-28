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
    public ImapTestService()
    {
    }

    public async Task TestImapConnectionAsync(CustomServerInformation serverInformation, bool allowSSLHandShake)
    {
        using var protocolLogStream = new MemoryStream();

        try
        {
            var poolOptions = ImapClientPoolOptions.CreateTestPool(
                serverInformation,
                () => new WinoProtocolLogger(protocolLogStream, MailProtocol.Imap));

            using (var clientPool = new ImapClientPool(poolOptions)
            {
                ThrowOnSSLHandshakeCallback = !allowSSLHandShake
            })
            {
                // This call will make sure that everything is authenticated + connected successfully.
                var client = await clientPool.GetClientAsync();

                clientPool.Release(client);
            }

            // Test SMTP connectivity.
            using var smtpProtocolLogger = new WinoProtocolLogger(protocolLogStream, MailProtocol.Smtp);
            using var smtpClient = new SmtpClient(smtpProtocolLogger);
            smtpClient.ServerCertificateValidationCallback = (_, certificate, _, sslPolicyErrors)
                => MailKitServerCertificateValidator.Validate(certificate, sslPolicyErrors, !allowSSLHandShake);

            if (!smtpClient.IsConnected)
                await smtpClient.ConnectAsync(serverInformation.OutgoingServer, int.Parse(serverInformation.OutgoingServerPort), MailKit.Security.SecureSocketOptions.Auto);

            if (!smtpClient.IsAuthenticated)
                await smtpClient.AuthenticateAsync(serverInformation.OutgoingServerUsername, serverInformation.OutgoingServerPassword);
        }
        catch (Exception ex)
        {
            var actualError = ex.GetBaseException().Message;
            var message = string.IsNullOrWhiteSpace(actualError)
                ? "IMAP/SMTP server validation failed."
                : $"IMAP/SMTP server validation failed: {actualError}";

            throw new ImapValidationException(
                message,
                Encoding.UTF8.GetString(protocolLogStream.ToArray()),
                ex);
        }
    }
}

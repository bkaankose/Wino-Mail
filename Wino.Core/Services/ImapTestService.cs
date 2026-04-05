using System.Threading.Tasks;
using MailKit.Net.Smtp;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Integration;

namespace Wino.Core.Services;

public class ImapTestService : IImapTestService
{
    public ImapTestService()
    {
    }

    public async Task TestImapConnectionAsync(CustomServerInformation serverInformation, bool allowSSLHandShake)
    {
        var poolOptions = ImapClientPoolOptions.CreateTestPool(serverInformation);

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
        using var smtpClient = new SmtpClient();

        if (!smtpClient.IsConnected)
            await smtpClient.ConnectAsync(serverInformation.OutgoingServer, int.Parse(serverInformation.OutgoingServerPort), MailKit.Security.SecureSocketOptions.Auto);

        if (!smtpClient.IsAuthenticated)
            await smtpClient.AuthenticateAsync(serverInformation.OutgoingServerUsername, serverInformation.OutgoingServerPassword);
    }
}

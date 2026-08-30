using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Pop3;
using MimeKit;
using Wino.Core.Diagnostics;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Integration;

public sealed class MailKitPop3ClientFactory : IPop3ClientFactory
{
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IServerCertificateTrustService _certificateTrustService;

    public MailKitPop3ClientFactory(
        IApplicationConfiguration applicationConfiguration,
        IServerCertificateTrustService certificateTrustService)
    {
        _applicationConfiguration = applicationConfiguration;
        _certificateTrustService = certificateTrustService;
    }

    public IPop3ClientAdapter Create(Guid accountId, bool enableProtocolLog)
    {
        var client = enableProtocolLog
            ? new Pop3Client(WinoProtocolLogger.CreateAccountLogger(
                _applicationConfiguration.ApplicationDataFolderPath,
                accountId,
                MailProtocol.Pop3))
            : new Pop3Client();

        return new MailKitPop3ClientAdapter(client, _certificateTrustService);
    }
}

public sealed class MailKitPop3ClientAdapter : IPop3ClientAdapter
{
    private readonly Pop3Client _client;
    private readonly IServerCertificateTrustService _certificateTrustService;

    public MailKitPop3ClientAdapter(Pop3Client client, IServerCertificateTrustService certificateTrustService = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _certificateTrustService = certificateTrustService;
    }

    public bool IsConnected => _client.IsConnected;
    public bool IsAuthenticated => _client.IsAuthenticated;
    public bool SupportsUids => _client.SupportsUids;
    public int Count => _client.Count;

    public Task ConnectAndAuthenticateAsync(CustomServerInformation serverInformation, CancellationToken cancellationToken = default)
        => MailKitPop3ConnectionPolicy.ConnectAndAuthenticateAsync(
            _client, serverInformation, _certificateTrustService, cancellationToken);

    public async Task<IReadOnlyList<string>> GetMessageUidsAsync(CancellationToken cancellationToken = default)
        => (await _client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false)).ToList();

    public Task<HeaderList> GetMessageHeadersAsync(int index, CancellationToken cancellationToken = default)
        => _client.GetMessageHeadersAsync(index, cancellationToken);

    public Task<MimeMessage> GetMessageAsync(int index, CancellationToken cancellationToken = default)
        => _client.GetMessageAsync(index, cancellationToken);

    public Task DeleteMessageAsync(int index, CancellationToken cancellationToken = default)
        => _client.DeleteMessageAsync(index, cancellationToken);

    public Task DisconnectAsync(bool commitDeletions, CancellationToken cancellationToken = default)
        => _client.DisconnectAsync(commitDeletions, cancellationToken);

    public void Dispose() => _client.Dispose();
}

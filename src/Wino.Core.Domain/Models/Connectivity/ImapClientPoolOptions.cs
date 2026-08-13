using System;
using MailKit;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Connectivity;

public class ImapClientPoolOptions
{
    public CustomServerInformation ServerInformation { get; }
    public bool IsTestPool { get; }
    public Func<IProtocolLogger> ProtocolLoggerFactory { get; }
    public IServerCertificateTrustService CertificateTrustService { get; }

    protected ImapClientPoolOptions(
        CustomServerInformation serverInformation,
        bool isTestPool,
        Func<IProtocolLogger> protocolLoggerFactory,
        IServerCertificateTrustService certificateTrustService)
    {
        ServerInformation = serverInformation;
        IsTestPool = isTestPool;
        ProtocolLoggerFactory = protocolLoggerFactory;
        CertificateTrustService = certificateTrustService;
    }

    public static ImapClientPoolOptions CreateDefault(
        CustomServerInformation serverInformation,
        Func<IProtocolLogger> protocolLoggerFactory = null,
        IServerCertificateTrustService certificateTrustService = null)
        => new(serverInformation, false, protocolLoggerFactory, certificateTrustService);

    public static ImapClientPoolOptions CreateTestPool(
        CustomServerInformation serverInformation,
        Func<IProtocolLogger> protocolLoggerFactory = null,
        IServerCertificateTrustService certificateTrustService = null)
        => new(serverInformation, true, protocolLoggerFactory, certificateTrustService);
}

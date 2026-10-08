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

    /// <summary>
    /// The account the pool connects for. While it needs attention the pool opens no
    /// connection and sends no keepalive. Null for connectivity tests.
    /// </summary>
    public MailAccount Account { get; }

    protected ImapClientPoolOptions(
        CustomServerInformation serverInformation,
        bool isTestPool,
        Func<IProtocolLogger> protocolLoggerFactory,
        IServerCertificateTrustService certificateTrustService,
        MailAccount account)
    {
        ServerInformation = serverInformation;
        IsTestPool = isTestPool;
        ProtocolLoggerFactory = protocolLoggerFactory;
        CertificateTrustService = certificateTrustService;
        Account = account;
    }

    public static ImapClientPoolOptions CreateDefault(
        CustomServerInformation serverInformation,
        Func<IProtocolLogger> protocolLoggerFactory = null,
        IServerCertificateTrustService certificateTrustService = null,
        MailAccount account = null)
        => new(serverInformation, false, protocolLoggerFactory, certificateTrustService, account);

    public static ImapClientPoolOptions CreateTestPool(
        CustomServerInformation serverInformation,
        Func<IProtocolLogger> protocolLoggerFactory = null,
        IServerCertificateTrustService certificateTrustService = null)
        => new(serverInformation, true, protocolLoggerFactory, certificateTrustService, null);
}

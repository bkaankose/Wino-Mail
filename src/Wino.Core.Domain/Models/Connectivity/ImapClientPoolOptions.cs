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

    /// <summary>Supplies bearer tokens when <see cref="ServerInformation"/> uses OAuth2. Null otherwise.</summary>
    public IOAuthTokenService OAuthTokenService { get; }

    /// <summary>The account id, or the setup session key for a test pool.</summary>
    public Guid OAuthTokenKey { get; }

    protected ImapClientPoolOptions(
        CustomServerInformation serverInformation,
        bool isTestPool,
        Func<IProtocolLogger> protocolLoggerFactory,
        IServerCertificateTrustService certificateTrustService,
        MailAccount account,
        IOAuthTokenService oauthTokenService,
        Guid oauthTokenKey)
    {
        ServerInformation = serverInformation;
        IsTestPool = isTestPool;
        ProtocolLoggerFactory = protocolLoggerFactory;
        CertificateTrustService = certificateTrustService;
        Account = account;
        OAuthTokenService = oauthTokenService;
        OAuthTokenKey = oauthTokenKey;
    }

    public static ImapClientPoolOptions CreateDefault(
        CustomServerInformation serverInformation,
        Func<IProtocolLogger> protocolLoggerFactory = null,
        IServerCertificateTrustService certificateTrustService = null,
        MailAccount account = null,
        IOAuthTokenService oauthTokenService = null)
        => new(serverInformation, false, protocolLoggerFactory, certificateTrustService, account, oauthTokenService, account?.Id ?? Guid.Empty);

    public static ImapClientPoolOptions CreateTestPool(
        CustomServerInformation serverInformation,
        Func<IProtocolLogger> protocolLoggerFactory = null,
        IServerCertificateTrustService certificateTrustService = null,
        IOAuthTokenService oauthTokenService = null,
        Guid oauthTokenKey = default)
        => new(serverInformation, true, protocolLoggerFactory, certificateTrustService, null, oauthTokenService, oauthTokenKey);
}

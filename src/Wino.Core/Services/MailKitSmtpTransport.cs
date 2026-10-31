using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MimeKit;
using Serilog;
using Wino.Core.Diagnostics;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration;

namespace Wino.Core.Services;

public sealed class MailKitSmtpTransport : ISmtpTransport
{
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IServerCertificateTrustService _certificateTrustService;
    private readonly IOAuthTokenService _oauthTokenService;
    private readonly ILogger _logger = Log.ForContext<MailKitSmtpTransport>();

    public MailKitSmtpTransport(
        IApplicationConfiguration applicationConfiguration,
        IServerCertificateTrustService certificateTrustService,
        IOAuthTokenService oauthTokenService = null)
    {
        _applicationConfiguration = applicationConfiguration;
        _certificateTrustService = certificateTrustService;
        _oauthTokenService = oauthTokenService;
    }

    public async Task<MimeMessage> SendAsync(
        MailAccount account,
        MimeMessage draftMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(account.ServerInformation);
        ArgumentNullException.ThrowIfNull(draftMessage);

        using var smtpClient = account.IsProtocolLogEnabled
            ? new SmtpClient(WinoProtocolLogger.CreateAccountLogger(
                _applicationConfiguration.ApplicationDataFolderPath,
                account.Id,
                MailProtocol.Smtp))
            : new SmtpClient();

        await MailKitSmtpConnectionPolicy.ConnectAndAuthenticateAsync(
            smtpClient,
            account.ServerInformation,
            _certificateTrustService,
            cancellationToken,
            _oauthTokenService,
            account.Id).ConfigureAwait(false);

        var smtpMessage = CreateSmtpMessage(draftMessage);
        await smtpClient.SendAsync(smtpMessage, cancellationToken).ConfigureAwait(false);

        try
        {
            await smtpClient.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "SMTP accepted a message, but disconnect failed for account {AccountId}.",
                account.Id);
        }

        return smtpMessage;
    }

    public static MimeMessage CreateSmtpMessage(MimeMessage draftMessage)
    {
        ArgumentNullException.ThrowIfNull(draftMessage);

        using var stream = new MemoryStream();
        draftMessage.WriteTo(stream);
        stream.Position = 0;

        var smtpMessage = MimeMessage.Load(stream);
        smtpMessage.Headers.Remove(Constants.WinoLocalDraftHeader);
        smtpMessage.Headers.Remove(Constants.LegacyWinoLocalDraftHeader);
        smtpMessage.Headers.Remove("X-Wino-Draft-Revision");
        return smtpMessage;
    }
}

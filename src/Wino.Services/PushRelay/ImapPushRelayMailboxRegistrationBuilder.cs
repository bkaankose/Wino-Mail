#nullable enable
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Services.PushRelay;

/// <summary>
/// Builds relay registrations for IMAP accounts. Wino never uses OAuth over IMAP, so <c>authMethod</c> is always
/// <c>password</c>. The fingerprint lets the registration service resend credentials only when they change.
/// </summary>
public sealed class ImapPushRelayMailboxRegistrationBuilder(IAccountService accountService, IPushRelayDeviceInfo? deviceInfo = null) : IPushRelayMailboxRegistrationBuilder
{
    private const string PasswordAuthMethod = "password";

    public MailProviderType ProviderType => MailProviderType.IMAP4;

    /// <summary>The relay watches IMAP for phones only; desktop IMAP stays on the app's own IDLE client and uploads nothing.</summary>
    private bool IsMobile => deviceInfo?.Describe().Platform is PushRelayPlatforms.Ios or PushRelayPlatforms.Android;

    public async Task<bool> IsEligibleAsync(MailAccount account, CancellationToken cancellationToken = default)
        => await BuildAsync(account, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<PushRelayMailboxRegistrationRequest?> BuildAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!IsMobile)
            return null;
        cancellationToken.ThrowIfCancellationRequested();

        if (account.ProviderType != MailProviderType.IMAP4
            || !account.IsMailAccessGranted
            || account.AttentionReason == AccountAttentionReason.InvalidCredentials)
        {
            return null;
        }

        var server = await accountService.GetAccountCustomServerInformationAsync(account.Id).ConfigureAwait(false);
        return Build(account, server);
    }

    internal static PushRelayMailboxRegistrationRequest? Build(MailAccount account, CustomServerInformation? server)
    {
        if (server is null
            || server.IncomingServerType != CustomIncomingServerType.IMAP4
            || string.IsNullOrWhiteSpace(server.IncomingServer)
            || string.IsNullOrEmpty(server.IncomingServerPassword))
        {
            return null;
        }

        var username = !string.IsNullOrWhiteSpace(server.IncomingServerUsername) ? server.IncomingServerUsername.Trim() : account.Address?.Trim();
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var host = server.IncomingServer.Trim();
        var port = ResolvePort(server.IncomingServerPort, server.IncomingServerSocketOption);
        var security = MapSecurity(server.IncomingServerSocketOption, port);
        var credentials = new PushRelayImapCredentials(host, port, security, username, server.IncomingServerPassword, PasswordAuthMethod);
        var clientAccountId = account.Id.ToString("D");

        return new PushRelayMailboxRegistrationRequest(
            new PushRelayCreateMailboxRequest(PushRelayProviders.Imap, clientAccountId, credentials),
            new PushRelayUpdateCredentialsRequest(credentials),
            Fingerprint(host, port, security, username, server.IncomingServerPassword));
    }

    internal static int ResolvePort(string? configuredPort, ImapConnectionSecurity security)
    {
        if (int.TryParse(configuredPort?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
            return port;

        return security is ImapConnectionSecurity.StartTls or ImapConnectionSecurity.None ? 143 : 993;
    }

    internal static string MapSecurity(ImapConnectionSecurity security, int port) => security switch
    {
        ImapConnectionSecurity.SslTls => PushRelayImapSecurity.Ssl,
        ImapConnectionSecurity.StartTls => PushRelayImapSecurity.StartTls,
        ImapConnectionSecurity.None => PushRelayImapSecurity.None,
        _ => port == 993 ? PushRelayImapSecurity.Ssl : PushRelayImapSecurity.StartTls
    };

    internal static string Fingerprint(string host, int port, string security, string username, string password)
    {
        var material = string.Join('\0', host, port.ToString(CultureInfo.InvariantCulture), security, username, password);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

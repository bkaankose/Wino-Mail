#nullable enable
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IMailService = MailKit.IMailService;
using MailKit.Security;
using AuthenticationException = MailKit.Security.AuthenticationException;
using Serilog;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Integration;

/// <summary>
/// Bearer-token SASL authentication shared by IMAP and SMTP for accounts that use OAuth2.
/// </summary>
internal static class MailKitOAuthAuthenticator
{
    private const string OAuthBearerMechanism = "OAUTHBEARER";
    private const string XOAuth2Mechanism = "XOAUTH2";

    private static readonly ILogger Logger = Log.ForContext(typeof(MailKitOAuthAuthenticator));

    /// <summary>
    /// Authenticates with a token from <paramref name="tokenService"/>. A rejected token is refreshed and tried once more
    /// before the <see cref="AuthenticationException"/> reaches the existing handlers.
    /// A grant that needs interactive sign-in surfaces as <see cref="AuthenticationException"/> so the account is marked
    /// for sign-in; an unreachable issuer surfaces as <see cref="IOException"/> so the sync retries on the next tick.
    /// </summary>
    public static async Task AuthenticateAsync(
        IMailService client,
        string? username,
        IOAuthTokenService? tokenService,
        Guid tokenKey,
        CancellationToken cancellationToken)
    {
        if (tokenService == null)
            throw new InvalidOperationException("OAuth sign-in is not available because no OAuth token service is registered.");

        if (tokenKey == Guid.Empty)
            throw new InvalidOperationException("OAuth sign-in requires an account or setup session key.");

        // RFC 7628 requires TLS; a bearer token sent in clear text could be replayed by anyone on the path.
        if (!client.IsSecure)
            throw new InvalidOperationException("OAuth sign-in requires an SSL/TLS or STARTTLS connection to the mail server.");

        var mechanismName = SelectMechanism(client);
        var userName = username ?? string.Empty;

        var accessToken = await GetTokenAsync(() => tokenService.GetAccessTokenAsync(tokenKey, cancellationToken)).ConfigureAwait(false);

        try
        {
            await client.AuthenticateAsync(CreateMechanism(mechanismName, userName, accessToken), cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (AuthenticationException ex) when (client.IsConnected)
        {
            Logger.Information("The server rejected the OAuth token for {TokenKey} ({Reason}). Refreshing once before retrying.", tokenKey, ex.Message);
        }

        accessToken = await GetTokenAsync(() => tokenService.RefreshNowAsync(tokenKey, cancellationToken)).ConfigureAwait(false);
        await client.AuthenticateAsync(CreateMechanism(mechanismName, userName, accessToken), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> GetTokenAsync(Func<Task<string>> tokenFactory)
    {
        try
        {
            return await tokenFactory().ConfigureAwait(false);
        }
        catch (OAuthGrantInvalidException ex)
        {
            throw new AuthenticationException(ex.Message, ex);
        }
        catch (OAuthTransientException ex)
        {
            // The message must not contain "authentication": ImapAuthenticationFailedHandler matches on that word.
            throw new IOException("The OAuth issuer could not be reached to obtain a sign-in token.", ex);
        }
    }

    private static string SelectMechanism(IMailService client)
    {
        if (client.AuthenticationMechanisms.Contains(OAuthBearerMechanism))
            return OAuthBearerMechanism;

        if (client.AuthenticationMechanisms.Contains(XOAuth2Mechanism))
            return XOAuth2Mechanism;

        // Some servers accept XOAUTH2 without advertising it.
        client.AuthenticationMechanisms.Add(XOAuth2Mechanism);
        return XOAuth2Mechanism;
    }

    private static SaslMechanism CreateMechanism(string mechanismName, string userName, string accessToken)
    {
        var credentials = new NetworkCredential(userName, accessToken);

        return mechanismName == OAuthBearerMechanism
            ? new SaslMechanismOAuthBearer(credentials)
            : new SaslMechanismOAuth2(credentials);
    }
}

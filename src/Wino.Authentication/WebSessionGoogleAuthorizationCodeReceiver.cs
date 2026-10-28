#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication;

/// <summary>
/// Google OAuth in the platform's web authentication sheet (<see cref="IWinoAccountBrowserAuthBroker"/>) with the
/// custom-scheme redirect of a Google iOS client (<c>com.googleusercontent.apps.&lt;id&gt;:/oauthredirect</c>, derived
/// from <see cref="IAuthenticatorConfig.GmailAuthenticatorClientId"/>). The Mac and iOS apps use it for Gmail mailboxes.
/// </summary>
public sealed class WebSessionGoogleAuthorizationCodeReceiver(IAuthenticatorConfig config, IWinoAccountBrowserAuthBroker broker) : IGoogleAuthorizationCodeReceiver
{
    public async Task<GoogleAuthorizationCode> ReceiveCodeAsync(Func<Uri, string, Uri> authorizationUriFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUriFactory);
        cancellationToken.ThrowIfCancellationRequested();

        var redirectUri = GoogleCustomSchemeRedirect.GetRedirectUri(config.GmailAuthenticatorClientId);
        var state = GoogleAuthorizationPkce.CreateState();
        var codeVerifier = GoogleAuthorizationPkce.CreateCodeVerifier();
        var authorizationUri = GoogleAuthorizationPkce.AppendChallenge(authorizationUriFactory(redirectUri, state), codeVerifier);

        var callback = await broker.AuthorizeAsync(authorizationUri, redirectUri.Scheme, cancellationToken).ConfigureAwait(false);
        var result = GoogleCustomSchemeRedirect.ParseCallback(callback, redirectUri, state);
        if (result.Code is null)
            throw new InvalidOperationException(result.Error == "invalid_state"
                ? "Google authorization returned an invalid state value."
                : $"Google authorization failed: {result.Error}");
        return new GoogleAuthorizationCode(result.Code, redirectUri, codeVerifier);
    }
}

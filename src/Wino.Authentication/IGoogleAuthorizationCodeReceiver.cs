using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Authentication;

/// <summary>
/// Runs the interactive part of Google OAuth for <see cref="GmailAuthenticator"/>: it chooses the
/// redirect URI, opens the authorization page and receives the redirect. Desktop heads use the
/// loopback receiver (<see cref="WinoGmailCodeReceiver"/>); a mobile head supplies a custom-scheme receiver.
/// </summary>
public interface IGoogleAuthorizationCodeReceiver
{
    /// <summary>
    /// Creates the redirect URI and a state value, builds the authorization page address with
    /// <paramref name="authorizationUriFactory"/> (redirect URI, state), adds PKCE with
    /// <see cref="GoogleAuthorizationPkce"/>, and returns the code once the redirect arrives with a matching state.
    /// Cancellation, including the user dismissing the flow, surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<GoogleAuthorizationCode> ReceiveCodeAsync(Func<Uri, string, Uri> authorizationUriFactory, CancellationToken cancellationToken);
}

/// <summary>The authorization code with the redirect URI and PKCE verifier the token exchange must repeat.</summary>
public sealed record GoogleAuthorizationCode(string Code, Uri RedirectUri, string CodeVerifier);

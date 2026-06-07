using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Authentication;
using Wino.Mail.WinUI.Dialogs;
using WinUIEx;

namespace Wino.Mail.WinUI.Authentication;

/// <summary>
/// Interactive OIDC sign-in implemented with an embedded WebView2 (the path Outlook takes), hosting
/// the IdP page in-app and intercepting the redirect navigation. Reuses a redirect the IdP already
/// trusts, so no new reply URL has to be registered. Overrides the generic loopback implementation
/// from Wino.Core in the WinUI container.
/// </summary>
public sealed class WebView2InteractiveOidcAuthenticator : IInteractiveOidcAuthenticator
{
    private readonly IOidcTokenClient _oidcTokenClient;

    public WebView2InteractiveOidcAuthenticator(IOidcTokenClient oidcTokenClient)
    {
        _oidcTokenClient = oidcTokenClient;
    }

    public async Task<OidcTokenSet> SignInAsync(OidcConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.RedirectUri))
            throw new OidcTokenException("A redirect URI is required for interactive sign-in.");

        var discovery = await _oidcTokenClient.GetDiscoveryDocumentAsync(configuration.Authority, cancellationToken).ConfigureAwait(false);
        var pkce = _oidcTokenClient.CreatePkcePair();
        var state = Guid.NewGuid().ToString("N");
        var authorizationUrl = _oidcTokenClient.BuildAuthorizationUrl(discovery, configuration, pkce.Challenge, state);

        var redirect = await ShowSignInDialogAsync(authorizationUrl, configuration.RedirectUri).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(redirect.Error))
            throw new OidcTokenException($"Authorization failed: {redirect.Error}.");

        if (string.IsNullOrEmpty(redirect.Code))
            throw new OidcTokenException("Sign-in was cancelled before completing.");

        if (!string.Equals(redirect.State, state, StringComparison.Ordinal))
            throw new OidcTokenException("Authorization state mismatch; possible CSRF. Sign-in aborted.");

        return await _oidcTokenClient
            .ExchangeAuthorizationCodeAsync(discovery, configuration, redirect.Code, pkce.Verifier, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<RedirectResult> ShowSignInDialogAsync(string authorizationUrl, string redirectUri)
    {
        var mainWindow = WinoApplication.MainWindow
            ?? throw new OidcTokenException("No active window is available to host the sign-in dialog.");

        var tcs = new TaskCompletionSource<RedirectResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The dialog must be created and shown on the UI thread.
        var enqueued = mainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new OAuthSignInDialog(authorizationUrl, redirectUri)
                {
                    XamlRoot = mainWindow.Content?.XamlRoot
                };

                await dialog.ShowAsync();
                tcs.TrySetResult(dialog.Result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
            tcs.TrySetException(new OidcTokenException("Failed to schedule the sign-in dialog on the UI thread."));

        return tcs.Task;
    }
}

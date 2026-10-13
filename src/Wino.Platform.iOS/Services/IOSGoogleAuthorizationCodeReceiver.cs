using AuthenticationServices;
using Foundation;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// Google OAuth through <see cref="ASWebAuthenticationSession"/> with the custom-scheme redirect of a Google iOS client
/// (<c>com.googleusercontent.apps.&lt;id&gt;:/oauthredirect</c>, derived from
/// <see cref="IAuthenticatorConfig.GmailAuthenticatorClientId"/>). The session catches the redirect itself, so the scheme
/// does not need an app URL handler; it is still listed in <c>CFBundleURLTypes</c>. The user closing the sheet, or
/// <paramref name="cancellationToken"/>, ends the flow with <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class IOSGoogleAuthorizationCodeReceiver(IAuthenticatorConfig config, IDispatcher dispatcher, IOSPresentationAnchorProvider anchorProvider) : IGoogleAuthorizationCodeReceiver
{
    public async Task<GoogleAuthorizationCode> ReceiveCodeAsync(Func<Uri, string, Uri> authorizationUriFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUriFactory);
        cancellationToken.ThrowIfCancellationRequested();

        var redirectUri = GoogleCustomSchemeRedirect.GetRedirectUri(config.GmailAuthenticatorClientId);
        var state = GoogleAuthorizationPkce.CreateState();
        var codeVerifier = GoogleAuthorizationPkce.CreateCodeVerifier();
        var authorizationUri = GoogleAuthorizationPkce.AppendChallenge(authorizationUriFactory(redirectUri, state), codeVerifier);

        var completion = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        ASWebAuthenticationSession? session = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            if (!anchorProvider.HasAnchor)
            {
                completion.TrySetException(new InvalidOperationException("Google authorization requires a visible window."));
                return;
            }
            var url = NSUrl.FromString(authorizationUri.AbsoluteUri);
            if (url is null)
            {
                completion.TrySetException(new InvalidOperationException("The Google authorization address is not a valid URL."));
                return;
            }
            session = CreateSession(url, redirectUri.Scheme, (callbackUrl, error) => Complete(completion, callbackUrl, error));
            session.PresentationContextProvider = anchorProvider;
            if (!session.Start())
                completion.TrySetException(new InvalidOperationException("The Google authorization session could not start."));
        }).ConfigureAwait(false);

        using var registration = cancellationToken.Register(() =>
        {
            if (completion.TrySetCanceled(cancellationToken))
                _ = dispatcher.ExecuteOnUIThread(() => session?.Cancel());
        });

        var callback = await completion.Task.ConfigureAwait(false);
        var result = GoogleCustomSchemeRedirect.ParseCallback(callback, redirectUri, state);
        if (result.Code is null)
            throw new InvalidOperationException(result.Error == "invalid_state"
                ? "Google authorization returned an invalid state value."
                : $"Google authorization failed: {result.Error}");
        return new GoogleAuthorizationCode(result.Code, redirectUri, codeVerifier);
    }

    private static ASWebAuthenticationSession CreateSession(NSUrl url, string scheme, ASWebAuthenticationSessionCompletionHandler handler)
    {
        // iOS 17.4 replaced the scheme initializer with ASWebAuthenticationSessionCallback.
        if (OperatingSystem.IsIOSVersionAtLeast(17, 4))
            return new ASWebAuthenticationSession(url, ASWebAuthenticationSessionCallback.Create(scheme), handler);
        return new ASWebAuthenticationSession(url, scheme, handler);
    }

    private static void Complete(TaskCompletionSource<Uri> completion, NSUrl? callbackUrl, NSError? error)
    {
        if (error is not null)
        {
            if (error.Domain == ASWebAuthenticationSessionErrorCode.CanceledLogin.GetDomain()?.ToString()
                && error.Code == (nint)(long)ASWebAuthenticationSessionErrorCode.CanceledLogin)
                completion.TrySetCanceled();
            else
                completion.TrySetException(new InvalidOperationException($"Google authorization failed: {error.LocalizedDescription}"));
            return;
        }

        var text = callbackUrl?.AbsoluteString;
        if (text is not null && Uri.TryCreate(text, UriKind.Absolute, out var callback))
            completion.TrySetResult(callback);
        else
            completion.TrySetException(new InvalidOperationException("Google authorization returned no redirect."));
    }
}

using AuthenticationServices;
using Foundation;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Apple.Services;

/// <summary>
/// Custom-scheme OAuth through <see cref="ASWebAuthenticationSession"/>, shared by the Mac and iOS apps: the Wino Account
/// API browser flow (Macs without the Sign in with Apple entitlement) and Google sign-in with the Wino Accounts iOS client.
/// The session catches the redirect itself, so the scheme needs no <c>CFBundleURLTypes</c> entry. The user closing the
/// sheet, or <c>cancellationToken</c>, ends the flow with <see cref="OperationCanceledException"/>. The head supplies the
/// presentation anchor (UIWindow or NSWindow) and whether one exists right now.
/// </summary>
public sealed class AppleWebAuthenticationBroker(IDispatcher dispatcher, IASWebAuthenticationPresentationContextProviding anchor, Func<bool> hasAnchor) : IWinoAccountBrowserAuthBroker
{
    public async Task<Uri> AuthorizeAsync(Uri authorizationUri, string callbackScheme, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackScheme);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        ASWebAuthenticationSession? session = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            if (!hasAnchor())
            {
                completion.TrySetException(new InvalidOperationException("Browser sign-in requires a visible window."));
                return;
            }
            var url = NSUrl.FromString(authorizationUri.AbsoluteUri);
            if (url is null)
            {
                completion.TrySetException(new InvalidOperationException("The sign-in address is not a valid URL."));
                return;
            }
            session = CreateSession(url, callbackScheme, (callbackUrl, error) => Complete(completion, callbackUrl, error));
            session.PresentationContextProvider = anchor;
            if (!session.Start())
                completion.TrySetException(new InvalidOperationException("The browser sign-in session could not start."));
        }).ConfigureAwait(false);

        using var registration = cancellationToken.Register(() =>
        {
            if (completion.TrySetCanceled(cancellationToken))
                _ = dispatcher.ExecuteOnUIThread(() => session?.Cancel());
        });

        return await completion.Task.ConfigureAwait(false);
    }

    private static ASWebAuthenticationSession CreateSession(NSUrl url, string scheme, ASWebAuthenticationSessionCompletionHandler handler)
    {
        // iOS 17.4 and macOS 14.4 replaced the scheme initializer with ASWebAuthenticationSessionCallback.
        if (OperatingSystem.IsIOSVersionAtLeast(17, 4) || OperatingSystem.IsMacOSVersionAtLeast(14, 4))
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
                completion.TrySetException(new InvalidOperationException($"Browser sign-in failed: {error.LocalizedDescription}"));
            return;
        }

        var text = callbackUrl?.AbsoluteString;
        if (text is not null && Uri.TryCreate(text, UriKind.Absolute, out var callback))
            completion.TrySetResult(callback);
        else
            completion.TrySetException(new InvalidOperationException("Browser sign-in returned no redirect."));
    }
}

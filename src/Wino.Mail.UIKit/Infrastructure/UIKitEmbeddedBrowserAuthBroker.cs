using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Mail.UIKit.Views.Dialogs;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Exchange modern-auth sign-in in a WKWebView sheet (<see cref="EmbeddedBrowserSignInViewController"/>) over the top-most
/// controller (the Mac MacEmbeddedBrowserAuthBroker). It shares nothing with the system-browser OAuth flows: no
/// ASWebAuthenticationSession, no Safari, no loopback listener and no URL scheme activation. One sign-in shows at a time.
/// </summary>
public sealed class UIKitEmbeddedBrowserAuthBroker(IDispatcher dispatcher, UIKitAuthenticationPresenter presenter) : IEmbeddedBrowserAuthBroker
{
    private readonly SemaphoreSlim _presentation = new(1, 1);

#if DEBUG
    // Debug bridge: "embedded-signin [START] [REDIRECT]" opens the sheet; "embedded-signin-result" reports how it ended;
    // "embedded-signin-cancel" cancels it through the caller's token.
    private Task<EmbeddedBrowserAuthResult>? _debugSignIn;
    private CancellationTokenSource? _debugCancellation;

    public void RegisterDebugCommands()
    {
        UIKitDebugBridge.Register("embedded-signin", args =>
        {
            var start = new Uri(args.Length > 0 ? args[0] : "https://example.com/");
            // example.com links to this page; following the link completes the sign-in with it as the redirect.
            var redirect = new Uri(args.Length > 1 ? args[1] : "https://www.iana.org/help/example-domains");
            _debugCancellation?.Dispose();
            _debugCancellation = new CancellationTokenSource();
            _debugSignIn = Task.Run(() => AuthenticateAsync(start, redirect, _debugCancellation.Token));
            return Task.FromResult("started");
        });
        UIKitDebugBridge.Register("embedded-signin-cancel", _ =>
        {
            _debugCancellation?.Cancel();
            return Task.FromResult("cancel requested");
        });
        UIKitDebugBridge.Register("embedded-signin-result", _ => Task.FromResult(_debugSignIn switch
        {
            null => "none",
            { IsCompletedSuccessfully: true } done => $"{done.Result.Status} {done.Result.RedirectUri}",
            { IsCanceled: true } => "canceled (OperationCanceledException)",
            { IsCompleted: true } failed => "failed " + failed.Exception?.GetBaseException().Message,
            _ => "pending"
        }));
    }
#endif

    public async Task<EmbeddedBrowserAuthResult> AuthenticateAsync(Uri startUri, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startUri);
        ArgumentNullException.ThrowIfNull(redirectUri);
        cancellationToken.ThrowIfCancellationRequested();

        await _presentation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EmbeddedBrowserSignInViewController? signIn = null;
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (presenter.Presenter() is not { } parent) return;
                signIn = new EmbeddedBrowserSignInViewController(startUri, redirectUri);
                signIn.Present(parent);
            }).ConfigureAwait(false);

            if (signIn is null)
                return EmbeddedBrowserAuthResult.Failed;

            EmbeddedBrowserAuthResult result;
            using (cancellationToken.Register(() => _ = dispatcher.ExecuteOnUIThread(() => signIn.Cancel())))
                result = await signIn.Completion.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _presentation.Release();
        }
    }
}

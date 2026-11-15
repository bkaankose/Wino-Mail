using AppKit;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Mail.MacOS.Views.Dialogs;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Exchange modern-auth sign-in in a WKWebView sheet (<see cref="EmbeddedBrowserSignInSheet"/>), presented on the main
/// thread over the key window. It shares nothing with the system-browser OAuth flows: no ASWebAuthenticationSession,
/// no default browser, no loopback listener and no URL scheme activation.
/// </summary>
public sealed class MacEmbeddedBrowserAuthBroker(IDispatcher dispatcher, Func<NSWindow?> owner) : IEmbeddedBrowserAuthBroker
{
    private readonly SemaphoreSlim _presentation = new(1, 1);

#if DEBUG
    // Debug bridge: "embedded-signin <start> <redirect>" opens the sheet; "embedded-signin-result" reports how it ended.
    private Task<EmbeddedBrowserAuthResult>? _debugSignIn;

    public void RegisterDebugCommands()
    {
        MacDebugBridge.Register("embedded-signin", args =>
        {
            var start = new Uri(args.Length > 0 ? args[0] : "https://example.com/");
            var redirect = new Uri(args.Length > 1 ? args[1] : "https://www.iana.org/help/example-domains");
            _debugSignIn = AuthenticateAsync(start, redirect, CancellationToken.None);
            return Task.FromResult("started");
        });
        MacDebugBridge.Register("embedded-signin-result", _ => Task.FromResult(_debugSignIn switch
        {
            null => "none",
            { IsCompletedSuccessfully: true } done => $"{done.Result.Status} {done.Result.RedirectUri}",
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
            EmbeddedBrowserSignInSheet? sheet = null;
            await dispatcher.ExecuteOnUIThread(() =>
            {
                var parent = ResolveOwner();
                if (parent is null) return;
                sheet = new EmbeddedBrowserSignInSheet(startUri, redirectUri);
                sheet.Present(parent);
            }).ConfigureAwait(false);

            if (sheet is null)
                return EmbeddedBrowserAuthResult.Failed;

            EmbeddedBrowserAuthResult result;
            using (cancellationToken.Register(() => _ = dispatcher.ExecuteOnUIThread(() => sheet.Cancel())))
                result = await sheet.Completion.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _presentation.Release();
        }
    }

    private NSWindow? ResolveOwner()
    {
        var key = NSApplication.SharedApplication.KeyWindow;
        if (key is { IsVisible: true, IsSheet: false } && key.AttachedSheet is null) return key;
        var fallback = owner() ?? NSApplication.SharedApplication.MainWindow;
        return fallback is { IsVisible: true } && fallback.AttachedSheet is null ? fallback : null;
    }
}

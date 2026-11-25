using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Exchange organisation sign-in in <see cref="EmbeddedBrowserSignInWindow"/>, over the active window (the Settings window
/// during setup). One sign-in at a time: a second call waits for the first, as on macOS. Nothing here uses the system
/// browser, a loopback listener or the <c>wino://</c> scheme.
/// </summary>
internal sealed class GtkEmbeddedBrowserAuthBroker(IDispatcher dispatcher, Func<Gtk.Window?> owner) : IEmbeddedBrowserAuthBroker
{
    private readonly SemaphoreSlim _presentation = new(1, 1);

    /// <summary>The open sign-in window, for checks. Main thread.</summary>
    public EmbeddedBrowserSignInWindow? Current { get; private set; }

    public async Task<EmbeddedBrowserAuthResult> AuthenticateAsync(Uri startUri, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startUri);
        ArgumentNullException.ThrowIfNull(redirectUri);
        cancellationToken.ThrowIfCancellationRequested();

        await _presentation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EmbeddedBrowserSignInWindow? window = null;
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (owner() is not { } parent) return;
                window = new EmbeddedBrowserSignInWindow(startUri, redirectUri);
                window.Present(parent);
                Current = window;
            }).ConfigureAwait(false);

            if (window is null) return EmbeddedBrowserAuthResult.Failed;

            EmbeddedBrowserAuthResult result;
            using (cancellationToken.Register(() => _ = dispatcher.ExecuteOnUIThread(window.Cancel)))
                result = await window.Completion.ConfigureAwait(false);

            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (ReferenceEquals(Current, window)) Current = null;
            }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _presentation.Release();
        }
    }
}

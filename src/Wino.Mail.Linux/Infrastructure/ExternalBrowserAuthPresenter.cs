using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Keeps <see cref="ExternalBrowserAuthDialog"/> open over the active window while a sign-in waits for its browser
/// redirect. A failure to present never blocks the sign-in: the session then does nothing.
/// </summary>
internal sealed class ExternalBrowserAuthPresenter(IDispatcher dispatcher, Func<Gtk.Window?> owner, IClipboardService clipboard) : IExternalBrowserAuthenticationPresenter
{
    public async Task<IExternalBrowserAuthenticationSession> ShowAsync(ExternalBrowserAuthenticationRequest request, Action cancelRequested)
    {
        ExternalBrowserAuthDialog? dialog = null;
        Gtk.Window? host = null;
        try
        {
            await dispatcher.ExecuteOnUIThread(() =>
            {
                host = owner();
                if (host is null) return;
                dialog = new ExternalBrowserAuthDialog(request, cancelRequested, text => clipboard.CopyTextAsync(text));
                dialog.Present(host);
                Current = dialog;
            });
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The browser sign-in dialog could not be presented.");
        }
        return new Session(dialog, host, dispatcher, this);
    }

    /// <summary>The open waiting dialog, for checks. Main thread.</summary>
    public ExternalBrowserAuthDialog? Current { get; private set; }

    private sealed class Session(ExternalBrowserAuthDialog? dialog, Gtk.Window? host, IDispatcher dispatcher, ExternalBrowserAuthPresenter presenter)
        : IExternalBrowserAuthenticationSession
    {
        public Task NotifyBrowserLaunchFailedAsync() => RunAsync(() => dialog?.ShowBrowserLaunchFailed());

        // On Wayland a present without an activation token may only mark the window as wanting attention; that is accepted.
        public Task NotifyRedirectReceivedAsync() => RunAsync(() => host?.Present());

        public async ValueTask DisposeAsync() => await RunAsync(() =>
        {
            dialog?.Close();
            if (ReferenceEquals(presenter.Current, dialog)) presenter.Current = null;
        });

        private async Task RunAsync(Action action)
        {
            try { await dispatcher.ExecuteOnUIThread(action); }
            catch (Exception exception) { Serilog.Log.Error(exception, "The browser sign-in dialog could not be updated."); }
        }
    }
}

using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Mail.Android.Views.Dialogs;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// Exchange modern-auth sign-in in a WebView dialog (<see cref="EmbeddedBrowserSignInDialog"/>) over the started
/// activity (the Mac MacEmbeddedBrowserAuthBroker). It shares nothing with the Custom Tab OAuth flows: no system
/// browser, no loopback listener and no wino:// redirect. One sign-in shows at a time.
/// </summary>
public sealed class AndroidEmbeddedBrowserAuthBroker(IDispatcher dispatcher, AndroidActivityTracker activities) : IEmbeddedBrowserAuthBroker
{
    private readonly SemaphoreSlim _presentation = new(1, 1);

    public async Task<EmbeddedBrowserAuthResult> AuthenticateAsync(Uri startUri, Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startUri);
        ArgumentNullException.ThrowIfNull(redirectUri);
        cancellationToken.ThrowIfCancellationRequested();

        await _presentation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EmbeddedBrowserSignInDialog? dialog = null;
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (activities.Current is not { IsFinishing: false, IsDestroyed: false } activity) return;
                dialog = new EmbeddedBrowserSignInDialog(startUri, redirectUri);
                dialog.Show(activity);
            }).ConfigureAwait(false);

            if (dialog is null)
                return EmbeddedBrowserAuthResult.Failed;

            EmbeddedBrowserAuthResult result;
            using (cancellationToken.Register(() => _ = dispatcher.ExecuteOnUIThread(() => dialog.Cancel())))
                result = await dialog.Completion.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _presentation.Release();
        }
    }
}

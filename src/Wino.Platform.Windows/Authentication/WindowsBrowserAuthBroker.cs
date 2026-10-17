#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Windows.Authentication;

/// <summary>
/// The Wino Account browser sign-in flow on Windows: the API's authorize page in the default browser (the same
/// launcher the Gmail sign-in uses), then the <c>wino-mail://auth-complete</c> protocol activation that the app's
/// activation router delivers to <see cref="WindowsExternalAuthCallbacks"/>. The browser tab stays open if the user
/// never returns; the shared service's timeout abandons the wait. Cancellation ends the wait with
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class WindowsBrowserAuthBroker(IExternalLauncher launcher, WindowsExternalAuthCallbacks callbacks) : IWinoAccountBrowserAuthBroker
{
    public async Task<Uri> AuthorizeAsync(Uri authorizationUri, string callbackScheme, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        cancellationToken.ThrowIfCancellationRequested();

        // Register the wait before the browser opens; a fast redirect must not race it.
        var wait = callbacks.WaitAsync(cancellationToken);
        if (wait.IsFaulted) await wait.ConfigureAwait(false);
        try
        {
            var launch = await launcher.LaunchUriAsync(authorizationUri, cancellationToken).ConfigureAwait(false);
            if (!launch.IsSuccess)
                throw new InvalidOperationException(launch.ErrorMessage ?? "Windows could not open the sign-in page in a browser.");
        }
        catch
        {
            _ = wait.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }

        return await wait.ConfigureAwait(false);
    }
}

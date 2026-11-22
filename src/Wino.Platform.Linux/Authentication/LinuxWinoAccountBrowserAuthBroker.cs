using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux.Authentication;

/// <summary>
/// The Wino Account browser sign-in on Linux: the API's authorize page in the default browser, then the
/// <c>wino://auth-complete</c> address delivered to <see cref="LinuxExternalAuthCallbacks"/>. The shared service's token
/// carries the timeout; cancellation ends the wait with <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class LinuxWinoAccountBrowserAuthBroker(IExternalLauncher launcher, LinuxExternalAuthCallbacks callbacks) : IWinoAccountBrowserAuthBroker
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
                throw new InvalidOperationException(launch.ErrorMessage ?? "The sign-in page could not be opened in a browser.");
        }
        catch
        {
            _ = wait.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }

        return await wait.ConfigureAwait(false);
    }
}

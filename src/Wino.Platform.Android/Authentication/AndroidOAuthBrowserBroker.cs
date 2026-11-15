using Android.App;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Authentication;

/// <summary>
/// Custom-scheme OAuth sign-in on Android: the provider's authorization page in the same Custom Tab as the Wino Account
/// flow, then the <c>wino://auth-complete/oauth</c> redirect that the head's redirect activity delivers to
/// <see cref="AndroidExternalAuthCallbacks"/>, which completes only an OAuth wait for that path.
/// </summary>
public sealed class AndroidOAuthBrowserBroker(IDispatcher dispatcher, Func<Activity?> currentActivity, AndroidExternalAuthCallbacks callbacks) : IOAuthBrowserBroker
{
    public async Task<Uri> AuthorizeAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        cancellationToken.ThrowIfCancellationRequested();

        using var launchFailed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Register the wait before the tab opens; a fast redirect must not race it.
        var wait = callbacks.WaitForOAuthAsync(launchFailed.Token);
        if (wait.IsFaulted) await wait.ConfigureAwait(false);
        try
        {
            await dispatcher.ExecuteOnUIThread(() => AndroidBrowserAuthBroker.LaunchCustomTab(currentActivity, authorizationUri)).ConfigureAwait(false);
        }
        catch
        {
            launchFailed.Cancel();
            _ = wait.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }

        return await wait.ConfigureAwait(false);
    }
}

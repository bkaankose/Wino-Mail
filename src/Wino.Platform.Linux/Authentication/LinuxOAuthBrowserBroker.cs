using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux.Authentication;

/// <summary>
/// Custom-scheme OAuth sign-in on Linux: the provider's page in the default browser, then the
/// <c>wino://auth-complete/oauth</c> address delivered to <see cref="LinuxExternalAuthCallbacks"/>. The waiting dialog
/// lets the user cancel or copy the address. The time limit is the shared <c>OAuthTokenService</c>'s.
/// </summary>
public sealed class LinuxOAuthBrowserBroker(
    IExternalLauncher launcher,
    LinuxExternalAuthCallbacks callbacks,
    Func<IExternalBrowserAuthenticationPresenter?> presenter) : IOAuthBrowserBroker
{
    public async Task<Uri> AuthorizeAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        cancellationToken.ThrowIfCancellationRequested();

        using var userCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Register the wait before the browser opens; a fast redirect must not race it.
        var wait = callbacks.WaitForOAuthAsync(userCancel.Token);
        if (wait.IsFaulted) await wait.ConfigureAwait(false);

        IExternalBrowserAuthenticationSession? session = null;
        try
        {
            if (presenter() is { } waitingUi)
            {
                session = await waitingUi.ShowAsync(
                    new ExternalBrowserAuthenticationRequest(authorizationUri.Host, authorizationUri),
                    () => TryCancel(userCancel)).ConfigureAwait(false);
            }

            var launch = await launcher.LaunchUriAsync(authorizationUri, userCancel.Token).ConfigureAwait(false);
            if (launch.Status == PlatformOperationStatus.Cancelled)
                throw new OperationCanceledException(userCancel.Token);

            if (!launch.IsSuccess)
            {
                if (session is null)
                    throw new OAuthSignInException(OAuthSignInFailureKind.Failed, launch.ErrorMessage ?? "The sign-in page could not be opened in a browser.");

                await session.NotifyBrowserLaunchFailedAsync().ConfigureAwait(false);
            }

            var callback = await wait.ConfigureAwait(false);
            if (session is not null)
                await session.NotifyRedirectReceivedAsync().ConfigureAwait(false);

            return callback;
        }
        catch
        {
            TryCancel(userCancel);
            _ = wait.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

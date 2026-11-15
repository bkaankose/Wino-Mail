#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Windows.Authentication;

/// <summary>
/// Custom-scheme OAuth sign-in on Windows: the provider's authorization page in the default browser, then the
/// <c>wino://auth-complete/oauth</c> protocol activation delivered to <see cref="WindowsExternalAuthCallbacks"/>.
/// The waiting dialog lets the user cancel or copy the address when the browser does not appear.
/// </summary>
public sealed class WindowsOAuthBrowserBroker(
    IExternalLauncher launcher,
    WindowsExternalAuthCallbacks callbacks,
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
            var waitingUi = presenter();
            if (waitingUi is not null)
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
                    throw new OAuthSignInException(OAuthSignInFailureKind.Failed, launch.ErrorMessage ?? "Windows could not open the sign-in page in a browser.");

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
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

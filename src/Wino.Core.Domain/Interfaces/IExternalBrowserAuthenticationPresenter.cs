using System;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Shows in-app progress while the user completes an OAuth sign-in in the external browser.
/// The presenter owns the UI only; the authenticator owns the authorization flow and closes
/// the session when the browser redirect arrives.
/// </summary>
public interface IExternalBrowserAuthenticationPresenter
{
    /// <summary>
    /// Presents the waiting UI for the given authorization request.
    /// </summary>
    /// <param name="request">Provider name and the authorization address the browser was sent to.</param>
    /// <param name="cancelRequested">Invoked when the user cancels from the UI.</param>
    /// <returns>A session handle. Disposing it closes the UI.</returns>
    Task<IExternalBrowserAuthenticationSession> ShowAsync(ExternalBrowserAuthenticationRequest request, Action cancelRequested);
}

/// <summary>
/// Live waiting UI for one external browser sign-in.
/// </summary>
public interface IExternalBrowserAuthenticationSession : IAsyncDisposable
{
    /// <summary>
    /// Tells the UI that the browser could not be launched so it can steer the user to the copy-link fallback.
    /// </summary>
    Task NotifyBrowserLaunchFailedAsync();

    /// <summary>
    /// Tells the UI that the browser redirect arrived so the app can return to the foreground.
    /// </summary>
    Task NotifyRedirectReceivedAsync();
}

/// <param name="ProviderDisplayName">User-facing provider name, for example "Google".</param>
/// <param name="AuthorizationUri">The full authorization address the browser was launched with.</param>
public sealed record ExternalBrowserAuthenticationRequest(string ProviderDisplayName, Uri AuthorizationUri);

using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Intelligence.ConsoleApp.Hosting;

/// <summary>
/// The console has no window to host the waiting dialog; it prints the address instead so the
/// operator can still finish the sign-in by hand when the browser does not open.
/// </summary>
internal sealed class ConsoleExternalBrowserAuthenticationPresenter : IExternalBrowserAuthenticationPresenter
{
    public Task<IExternalBrowserAuthenticationSession> ShowAsync(ExternalBrowserAuthenticationRequest request, Action cancelRequested)
        => Task.FromResult<IExternalBrowserAuthenticationSession>(new Session(request));

    private sealed class Session(ExternalBrowserAuthenticationRequest request) : IExternalBrowserAuthenticationSession
    {
        public Task NotifyBrowserLaunchFailedAsync()
        {
            ConsoleOutput.Muted($"The browser could not be opened. Open this address manually: {request.AuthorizationUri.AbsoluteUri}");
            return Task.CompletedTask;
        }

        public Task NotifyRedirectReceivedAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

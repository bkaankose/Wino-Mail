using Microsoft.Identity.Client;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Platform.Linux.Authentication;

/// <summary>The one message every sign-in surface reports until Linux sign-in exists.</summary>
internal static class LinuxSignIn
{
    public const string Unavailable = "Interactive sign-in is not available on Linux yet.";

    public static NotSupportedException Exception() => new(Unavailable);
}

/// <summary>Outlook (MSAL) has no client on Linux yet: no token cache and no interactive sign-in.</summary>
public sealed class LinuxOutlookAuthenticationHost : IOutlookAuthenticationHost
{
    public IPublicClientApplication Client => throw LinuxSignIn.Exception();

    public bool CanAuthenticateInteractively => false;

    public Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default) => Task.FromException(LinuxSignIn.Exception());

    public Task<AuthenticationResult> AcquireTokenInteractiveAsync(IEnumerable<string> scopes, IAccount account, string loginHint,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<AuthenticationResult>(LinuxSignIn.Exception());
    }

    public Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class LinuxGoogleAuthorizationCodeReceiver : IGoogleAuthorizationCodeReceiver
{
    public Task<GoogleAuthorizationCode> ReceiveCodeAsync(Func<Uri, string, Uri> authorizationUriFactory, CancellationToken cancellationToken)
    {
        return Task.FromException<GoogleAuthorizationCode>(LinuxSignIn.Exception());
    }
}

public sealed class LinuxEmbeddedBrowserAuthBroker : IEmbeddedBrowserAuthBroker
{
    public Task<EmbeddedBrowserAuthResult> AuthenticateAsync(Uri startUri, Uri redirectUri, CancellationToken cancellationToken)
    {
        return Task.FromException<EmbeddedBrowserAuthResult>(LinuxSignIn.Exception());
    }
}

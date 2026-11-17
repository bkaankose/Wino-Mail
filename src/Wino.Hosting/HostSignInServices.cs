using Microsoft.Identity.Client;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Hosting;

/// <summary>The one message every sign-in surface of the host reports.</summary>
internal static class HostSignIn
{
    public const string Unavailable = "Interactive sign-in is not available in this host.";

    public static NotSupportedException Exception() => new(Unavailable);
}

/// <summary>Outlook (MSAL) has no client here: no token cache and no interactive sign-in.</summary>
internal sealed class HostOutlookAuthenticationHost(HostInteractionLog log) : IOutlookAuthenticationHost
{
    public IPublicClientApplication Client => throw HostSignIn.Exception();

    public bool CanAuthenticateInteractively => false;

    public Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default) => Task.FromException(HostSignIn.Exception());

    public Task<AuthenticationResult> AcquireTokenInteractiveAsync(IEnumerable<string> scopes, IAccount account, string loginHint,
        CancellationToken cancellationToken = default)
    {
        log.Record(HostInteractionKind.SignIn, "Outlook", loginHint, HostSignIn.Unavailable);
        return Task.FromException<AuthenticationResult>(HostSignIn.Exception());
    }

    public Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class HostGoogleAuthorizationCodeReceiver(HostInteractionLog log) : IGoogleAuthorizationCodeReceiver
{
    public Task<GoogleAuthorizationCode> ReceiveCodeAsync(Func<Uri, string, Uri> authorizationUriFactory, CancellationToken cancellationToken)
    {
        log.Record(HostInteractionKind.SignIn, "Google", null, HostSignIn.Unavailable);
        return Task.FromException<GoogleAuthorizationCode>(HostSignIn.Exception());
    }
}

internal sealed class HostOAuthBrowserBroker(HostInteractionLog log) : IOAuthBrowserBroker
{
    public Task<Uri> AuthorizeAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        log.Record(HostInteractionKind.SignIn, "OAuth browser", authorizationUri?.Host, HostSignIn.Unavailable);
        return Task.FromException<Uri>(HostSignIn.Exception());
    }
}

internal sealed class HostEmbeddedBrowserAuthBroker(HostInteractionLog log) : IEmbeddedBrowserAuthBroker
{
    public Task<EmbeddedBrowserAuthResult> AuthenticateAsync(Uri startUri, Uri redirectUri, CancellationToken cancellationToken)
    {
        log.Record(HostInteractionKind.SignIn, "Embedded browser", startUri?.Host, HostSignIn.Unavailable);
        return Task.FromException<EmbeddedBrowserAuthResult>(HostSignIn.Exception());
    }
}

internal sealed class HostWinoAccountBrowserAuthBroker(HostInteractionLog log) : IWinoAccountBrowserAuthBroker
{
    public Task<Uri> AuthorizeAsync(Uri authorizationUri, string callbackScheme, CancellationToken cancellationToken)
    {
        log.Record(HostInteractionKind.SignIn, "Wino Account browser", authorizationUri?.Host, HostSignIn.Unavailable);
        return Task.FromException<Uri>(HostSignIn.Exception());
    }
}

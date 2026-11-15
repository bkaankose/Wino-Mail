using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Platform.Apple.Services;

/// <summary>
/// Custom-scheme OAuth sign-in for IMAP and SMTP on the Mac and iOS: the same <c>ASWebAuthenticationSession</c>
/// sheet as <see cref="AppleWebAuthenticationBroker"/> with the <c>wino</c> scheme. Each session returns its redirect to
/// the session that opened it, so no callback split is needed here; this broker only keeps OAuth sign-ins to one at a time.
/// </summary>
public sealed class AppleOAuthBrowserBroker(AppleWebAuthenticationBroker webAuthentication) : IOAuthBrowserBroker
{
    private int _active;

    public async Task<Uri> AuthorizeAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new OAuthSignInException(OAuthSignInFailureKind.AnotherSignInInProgress, "Another sign-in is in progress.");

        try
        {
            return await webAuthentication.AuthorizeAsync(authorizationUri, WinoAccountExternalAuth.CallbackScheme, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }
}

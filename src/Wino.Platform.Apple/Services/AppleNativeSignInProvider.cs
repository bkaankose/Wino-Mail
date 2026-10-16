using System.Text;
using AuthenticationServices;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Platform.Apple.Services;

/// <summary>
/// Sign in with Apple through <see cref="ASAuthorizationController"/>, shared by the iOS and Mac apps (one App ID,
/// <c>com.winomail.macos</c>, so the API accepts the same identity-token audience from both). The head supplies the
/// presentation anchor (a UIWindow or NSWindow) through <paramref name="anchor"/>. Only builds that carry the
/// <c>com.apple.developer.applesignin</c> entitlement register this provider; the others use the browser flow.
/// Apple returns the user's name on the first authorization only; later sign-ins carry just the tokens.
/// </summary>
public sealed class AppleNativeSignInProvider(IDispatcher dispatcher, IASAuthorizationControllerPresentationContextProviding anchor) : IAppleNativeSignInProvider
{
    /// <summary>Shows Apple's sheet; null when the user dismissed it.</summary>
    public async Task<AppleSignInCredential?> RequestCredentialAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<AppleSignInCredential?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The controller holds its delegate weakly; both live here until the sheet completes.
        ASAuthorizationController? controller = null;
        Completion? handler = null;

        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            var request = new ASAuthorizationAppleIdProvider().CreateRequest();
            request.RequestedScopes = [ASAuthorizationScope.Email, ASAuthorizationScope.FullName];
            handler = new Completion(completion);
            controller = new ASAuthorizationController([request])
            {
                Delegate = handler,
                PresentationContextProvider = anchor
            };
            controller.PerformRequests();
        }).ConfigureAwait(false);

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            GC.KeepAlive(controller);
            GC.KeepAlive(handler);
        }
    }

    private sealed class Completion(TaskCompletionSource<AppleSignInCredential?> completion) : ASAuthorizationControllerDelegate
    {
        public override void DidComplete(ASAuthorizationController controller, ASAuthorization authorization)
        {
            var credential = authorization.GetCredential<ASAuthorizationAppleIdCredential>();
            if (credential is null)
            {
                completion.TrySetException(new WinoAccountExternalSignInException(ApiErrorCodes.ExternalLoginInvalid, "Apple returned no Apple ID credential."));
                return;
            }

            var identityToken = Utf8(credential.IdentityToken);
            var authorizationCode = Utf8(credential.AuthorizationCode);
            if (identityToken is null || authorizationCode is null)
            {
                completion.TrySetException(new WinoAccountExternalSignInException(ApiErrorCodes.ExternalLoginInvalid, "Apple returned no identity token or authorization code."));
                return;
            }

            var name = credential.FullName;
            completion.TrySetResult(new AppleSignInCredential(identityToken, authorizationCode, Clean(name?.GivenName), Clean(name?.FamilyName)));
        }

        public override void DidComplete(ASAuthorizationController controller, NSError error)
        {
            if (error.Domain == ASAuthorizationError.Canceled.GetDomain()?.ToString()
                && error.Code == (nint)(long)ASAuthorizationError.Canceled)
            {
                completion.TrySetResult(null);
                return;
            }

            // 1000 (Unknown) is what a build without the applesignin entitlement gets; keep Apple's text for the alert.
            completion.TrySetException(new WinoAccountExternalSignInException(WinoAccountClientErrorCodes.ExternalSignInFailed, $"{error.LocalizedDescription} ({error.Code})"));
        }

        private static string? Utf8(NSData? data)
        {
            if (data is null || data.Length == 0) return null;
            var text = Encoding.UTF8.GetString(data.ToArray());
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

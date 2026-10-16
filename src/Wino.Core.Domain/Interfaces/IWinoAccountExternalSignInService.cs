#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Signs in to a Wino Account with an external identity provider. The sign-in dialogs show a button per available
/// provider and call this. Each platform supplies the native pieces (<see cref="IAppleNativeSignInProvider"/>,
/// <see cref="IGoogleNativeSignInProvider"/>) or the API browser flow (<see cref="IWinoAccountBrowserAuthBroker"/>).
/// A user who dismisses the provider's UI gets a failure with <see cref="WinoAccountClientErrorCodes.SignInCancelled"/>,
/// which the dialogs ignore.
/// </summary>
public interface IWinoAccountExternalSignInService
{
    /// <summary>True when this build can sign in with Apple (natively or through the browser flow).</summary>
    bool IsAppleAvailable { get; }

    /// <summary>True when this build can sign in with Google (natively or through the browser flow).</summary>
    bool IsGoogleAvailable { get; }

    Task<WinoAccountOperationResult> SignInWithAppleAsync(CancellationToken cancellationToken = default);

    Task<WinoAccountOperationResult> SignInWithGoogleAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The platform half of native Sign in with Apple: shows Apple's sheet and returns the credential, or null when the
/// user dismissed it. Device-side failures throw <see cref="WinoAccountExternalSignInException"/>.
/// </summary>
public interface IAppleNativeSignInProvider
{
    Task<AppleSignInCredential?> RequestCredentialAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The platform half of native Google sign-in: obtains a Google ID token for one of the Wino Accounts Google clients
/// (<see cref="WinoAccountExternalAuth.GoogleAppleClientId"/>, <see cref="WinoAccountExternalAuth.GoogleAndroidServerClientId"/>),
/// or null when the user dismissed the sign-in. Device-side failures throw <see cref="WinoAccountExternalSignInException"/>.
/// </summary>
public interface IGoogleNativeSignInProvider
{
    Task<string?> RequestIdTokenAsync(CancellationToken cancellationToken);
}

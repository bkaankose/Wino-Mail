#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Issues relay tokens through the Wino Mail API (<c>POST /api/v1/relay/token</c>). It is the only relay code that
/// talks to the Wino API. Tokens are cached until two minutes before they expire, and concurrent callers share one call.
/// </summary>
/// <remarks>
/// Two paths exist. The device path registers this install as an attested device and gets tokens with its credential;
/// it needs no Wino Account. The user path uses the signed-in Wino Account and is taken once the device is linked to
/// it, or as the fallback when the platform must attest but cannot (Simulator, unknown Android build).
/// </remarks>
public interface IPushRelayTokenProvider
{
    /// <summary>
    /// This install's device credential, registering it with the API first when needed. Null when the device path is
    /// unavailable: the platform must attest and cannot, or the API refused the registration.
    /// </summary>
    Task<PushRelayDeviceCredential?> GetDeviceCredentialAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when no relay token can be issued without a Wino Account: the device path is unavailable here and no
    /// session exists. The registration service reports <c>SignInRequired</c> then.
    /// </summary>
    Task<bool> RequiresSignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Links the device to the signed-in Wino Account. Returns false when there is no device or no session.</summary>
    Task<bool> LinkDeviceAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the Wino Account from the device after sign-out. The device keeps its credential and registrations.</summary>
    Task UnlinkDeviceAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the device credential (the API or relay no longer knows the device). The next call registers anew.</summary>
    Task ForgetDeviceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a usable relay token. Throws <see cref="Exceptions.PushRelayApiException"/> with
    /// <see cref="PushRelayErrorCodes.SignInRequired"/> when no Wino Account can get one, or
    /// <see cref="PushRelayErrorCodes.ServiceUnavailable"/> when the API cannot issue one now.
    /// </summary>
    Task<PushRelayAccessToken> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the cached token so the next call asks the API again.</summary>
    void Invalidate();

    /// <summary>The <c>wr_mbx</c> claim of the last issued token, when known.</summary>
    int? MailboxLimit { get; }
}

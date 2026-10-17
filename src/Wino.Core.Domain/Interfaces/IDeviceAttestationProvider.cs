#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// One attestation statement for the API's device registration. App Attest fills <see cref="KeyId"/> and
/// <see cref="AttestationObject"/> (base64); Play Integrity fills <see cref="Token"/>; <see cref="DeviceAttestationKind.None"/>
/// fills nothing.
/// </summary>
public sealed record DeviceAttestation(DeviceAttestationKind Kind, string? KeyId = null, string? AttestationObject = null, string? Token = null)
{
    public static DeviceAttestation None { get; } = new(DeviceAttestationKind.None);

    /// <summary>Never prints the attestation material.</summary>
    public override string ToString() => $"DeviceAttestation {{ Kind = {Kind}, HasKey = {KeyId is not null} }}";
}

/// <summary>
/// Platform proof that this is the genuine Wino app on a real device. Heads without a platform service (Windows,
/// macOS) register a provider whose <see cref="Kind"/> is <see cref="DeviceAttestationKind.None"/>.
/// </summary>
public interface IDeviceAttestationProvider
{
    DeviceAttestationKind Kind { get; }

    /// <summary>False in the iOS Simulator, on macOS, and on Android builds the Play Store does not know.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Creates a fresh attestation bound to the API-issued <paramref name="nonce"/>. App Attest uses SHA-256 of the UTF-8
    /// nonce as the client data hash. Throws when the platform refuses.
    /// </summary>
    Task<DeviceAttestation> AttestAsync(string nonce, CancellationToken cancellationToken = default);

    /// <summary>
    /// App Attest only: an assertion (base64) with the key <paramref name="keyId"/> whose client data hash is SHA-256 of
    /// <paramref name="clientData"/>. Other kinds return null.
    /// </summary>
    Task<string?> CreateAssertionAsync(string keyId, byte[] clientData, CancellationToken cancellationToken = default);
}

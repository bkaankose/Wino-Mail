#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// One attestation statement for the API's device registration. App Attest fills <see cref="KeyId"/> and
/// <see cref="AttestationObject"/> (base64); Play Integrity fills <see cref="Token"/>; a bound key fills <see cref="KeyId"/>
/// (local only, never sent), <see cref="PublicKey"/> (base64 DER SubjectPublicKeyInfo) and <see cref="Signature"/> (base64
/// ECDSA signature over the UTF-8 nonce); <see cref="DeviceAttestationKind.None"/> fills nothing.
/// </summary>
public sealed record DeviceAttestation(DeviceAttestationKind Kind, string? KeyId = null, string? AttestationObject = null, string? Token = null,
    string? PublicKey = null, string? Signature = null)
{
    public static DeviceAttestation None { get; } = new(DeviceAttestationKind.None);

    /// <summary>Never prints the attestation material.</summary>
    public override string ToString() => $"DeviceAttestation {{ Kind = {Kind}, HasKey = {KeyId is not null} }}";
}

/// <summary>
/// Platform proof that this is the genuine Wino app on a real device (iOS, Android), or a machine-held key that proves
/// continuity (Windows, macOS: <see cref="DeviceAttestationKind.BoundKey"/>).
/// </summary>
public interface IDeviceAttestationProvider
{
    DeviceAttestationKind Kind { get; }

    /// <summary>False in the iOS Simulator and on Android builds the Play Store does not know.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Creates a fresh attestation bound to the API-issued <paramref name="nonce"/>. App Attest uses SHA-256 of the UTF-8
    /// nonce as the client data hash. Throws when the platform refuses.
    /// </summary>
    Task<DeviceAttestation> AttestAsync(string nonce, CancellationToken cancellationToken = default);

    /// <summary>
    /// App Attest: an assertion (base64) with the key <paramref name="keyId"/> whose client data hash is SHA-256 of
    /// <paramref name="clientData"/>. Bound key: an ECDSA P-256 SHA-256 signature (base64) over <paramref name="clientData"/>.
    /// Null when the key is gone; other kinds return null.
    /// </summary>
    Task<string?> CreateAssertionAsync(string keyId, byte[] clientData, CancellationToken cancellationToken = default);
}

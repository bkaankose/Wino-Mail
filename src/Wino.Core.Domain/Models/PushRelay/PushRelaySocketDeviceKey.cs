#nullable enable
using System;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>
/// This device's P-256 key for relay socket payload encryption. <see cref="Pkcs8PrivateKey"/> is the PKCS#8 private key as
/// the platform store keeps it; <see cref="PublicKeySpkiBase64"/> is the base64 SubjectPublicKeyInfo the relay receives as
/// <c>socketPublicKey</c>. <see cref="ToString"/> never prints the private key.
/// </summary>
public sealed record PushRelaySocketDeviceKey(byte[] Pkcs8PrivateKey, string PublicKeySpkiBase64)
{
    public override string ToString() => $"PushRelaySocketDeviceKey {{ PublicKeySpkiBase64 = {PublicKeySpkiBase64} }}";

    /// <summary>SHA-256 of the public key, used to detect a key change between registration passes.</summary>
    public string PublicKeyFingerprint
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(PublicKeySpkiBase64)));
}

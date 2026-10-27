namespace Wino.Core.Domain.Enums;

/// <summary>How this install proved to the Wino Mail API that it is the genuine Wino app on a real device.</summary>
public enum DeviceAttestationKind
{
    /// <summary>No attestation (a desktop whose key store failed, or a device where the platform service is unavailable).</summary>
    None,

    /// <summary>Apple App Attest (iOS and iPadOS).</summary>
    AppAttest,

    /// <summary>Google Play Integrity (Android, Play-distributed builds).</summary>
    PlayIntegrity,

    /// <summary>A P-256 key this machine holds (Windows CNG, macOS Secure Enclave or Keychain) that signs every credential call.</summary>
    BoundKey
}

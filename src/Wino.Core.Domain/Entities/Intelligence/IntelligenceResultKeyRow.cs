using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Intelligence;

/// <summary>
/// The device key the server encrypts intelligence results to. Exists only while the Wino
/// account has the Wino Intelligence add-on, so results stored on the server can be read by
/// this device and nobody else.
/// Only <c>IntelligenceResultKeyStore</c> reads <see cref="PrivateKeyProtected"/>; it never
/// appears in logs, snapshots or exports.
/// </summary>
[Table("IntelligenceResultKey")]
public sealed class IntelligenceResultKeyRow
{
    /// <summary>"dev-{yyyyMM}-{first 8 hex of SHA-256(SubjectPublicKeyInfo)}".</summary>
    [PrimaryKey]
    public string KeyId { get; set; } = string.Empty;

    [Indexed]
    public Guid WinoUserId { get; set; }

    /// <summary>SubjectPublicKeyInfo PEM. Sent in the upload manifest.</summary>
    public string PublicKeyPem { get; set; } = string.Empty;

    /// <summary>PKCS#8 private key bytes wrapped with DPAPI for the current Windows user.</summary>
    public byte[] PrivateKeyProtected { get; set; } = [];

    /// <summary>Lowercase hex SHA-256 of the SubjectPublicKeyInfo.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    /// <summary><see cref="IntelligenceResultKeyStatuses"/>.</summary>
    public string Status { get; set; } = IntelligenceResultKeyStatuses.Active;
}

public static class IntelligenceResultKeyStatuses
{
    public const string Active = "active";
}

using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// A single S/MIME signature found on a message, flattened to serializable data so it can
/// cross the RPC pipe. Produced by the companion during S/MIME render preparation.
/// </summary>
public record SmimeSignatureInfo(
    string SignerName,
    string SignerFingerprint,
    DateTime CertificateCreationDate,
    DateTime CertificateExpirationDate,
    bool IsValid);

/// <summary>
/// Result of preparing an S/MIME protected message for rendering. The companion decrypts
/// and/or verifies the message and writes the extracted inner MIME next to the original
/// one in the shared MIME storage; the UI renders that file instead of running any
/// cryptography itself.
/// </summary>
/// <param name="IsEncrypted">Message body was S/MIME enveloped and has been decrypted.</param>
/// <param name="IsSigned">Message carried one or more S/MIME signatures.</param>
/// <param name="ProcessedMimeFileName">
/// File name (relative to the message's MIME resource directory) of the decrypted/extracted
/// message to render. Null when the message is not S/MIME protected.
/// </param>
/// <param name="Signatures">Verification results for each signature found.</param>
public record SmimeRenderInfo(
    bool IsEncrypted,
    bool IsSigned,
    string ProcessedMimeFileName,
    List<SmimeSignatureInfo> Signatures)
{
    public static SmimeRenderInfo NotProtected { get; } = new(false, false, null, []);
}

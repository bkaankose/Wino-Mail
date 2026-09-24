#nullable enable
using System;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// The server transport public key uploads are encrypted to.
/// TODO: replace with the Contracts 3.0.0-alpha.1 IntelligenceTransportKeyDto when it is published.
/// </summary>
public sealed record IntelligenceTransportKeyDto(string KeyId, string PublicKeyPem, DateTimeOffset? NotAfterUtc);

/// <summary>
/// One result page as the server sent it: an encoded content envelope encrypted to the job's
/// device result key, or plain JSON for a job submitted before results were encrypted.
/// </summary>
public sealed record MailIntelligenceResultPayload(byte[] Content, bool IsEncrypted);

/// <summary>
/// Error codes the privacy rework adds to the API.
/// TODO: replace with ApiErrorCodes from Contracts 3.0.0-alpha.1 when it is published.
/// </summary>
public static class MailIntelligenceErrorCodes
{
    public const string ResultKeyInvalid = "INTELLIGENCE_RESULT_KEY_INVALID";
    public const string EnvelopeKeyUnknown = "INTELLIGENCE_ENVELOPE_KEY_UNKNOWN";
    public const string JobExpired = "INTELLIGENCE_JOB_EXPIRED";
}

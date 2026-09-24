#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

// TODO: every type in this file is an app-local copy of Contracts 3.0.0-alpha.1
// (Wino.Mail.Contracts.Intelligence and ApiErrorCodes). Delete it once that package is published.
namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>The server transport public key uploads are encrypted to.</summary>
public sealed record IntelligenceTransportKeyDto(string KeyId, string PublicKeyPem, DateTimeOffset? NotAfterUtc);

/// <summary>
/// One result page of a job bound to a device result key. The envelope holds the stage's
/// page DTO; page count, digest and key id sit outside it so they can be checked first.
/// </summary>
public sealed record EncryptedResultPageDto(
    int FormatVersion,
    string Stage,
    int PageIndex,
    int PageCount,
    string Digest,
    string ResultKeyId,
    byte[] Envelope);

/// <summary>Routes bound into result envelope authenticated data. Must match the server.</summary>
public static class MailIntelligenceResultRoutes
{
    public static string Page(Guid mailboxId, Guid jobId, string stageId, int pageIndex)
        => $"/api/v2/ai/intelligence/mailboxes/{mailboxId:D}/jobs/{jobId:D}/results/{stageId}/page-{pageIndex:D5}";
}

/// <summary>
/// Stage digest for encrypted jobs, computed over ciphertext: lowercase hex SHA-256 of each
/// page's lowercase hex SHA-256 followed by a line feed, in page order.
/// </summary>
public static class MailIntelligenceResultDigest
{
    public static string PageHash(ReadOnlySpan<byte> envelope)
        => Convert.ToHexStringLower(SHA256.HashData(envelope));

    public static string Compute(IEnumerable<string> pageHashes)
    {
        var builder = new StringBuilder();
        foreach (var hash in pageHashes)
        {
            builder.Append(hash).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

/// <summary>
/// Stage id of the second phase. The API accepts only this id; the published package still
/// names it "summarization".
/// </summary>
public static class MailIntelligenceStageIdsV3
{
    public const string Enrichment = "enrichment";
}

/// <summary>Error codes and statuses the privacy rework adds to the API.</summary>
public static class MailIntelligenceErrorCodes
{
    public const string ResultKeyInvalid = "INTELLIGENCE_RESULT_KEY_INVALID";
    public const string EnvelopeKeyUnknown = "INTELLIGENCE_ENVELOPE_KEY_UNKNOWN";
    public const string JobExpired = "INTELLIGENCE_JOB_EXPIRED";

    /// <summary>Job status for a job whose unacknowledged results the server deleted.</summary>
    public const string ExpiredJobStatus = "expired";
}

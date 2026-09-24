#nullable enable
using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

/// <summary>
/// Downloads one result page and opens it with the job's device result key. Plain pages are
/// accepted only for a job that was never bound to a key; a keyed job whose page arrives
/// unencrypted, or encrypted to another key, stage or page, is refused rather than trusted.
/// </summary>
public sealed class MailIntelligenceResultPageReader(
    IWinoAccountApiClient apiClient,
    IIntelligenceResultKeyStore keys)
{
    public Task<MailIntelligenceResultPage<ClassificationResultPageDto>> ReadClassificationAsync(
        MailIntelligenceJobState job, int page, CancellationToken cancellationToken)
        => ReadAsync(job, MailIntelligenceStageIds.Classification, page,
            WinoAccountApiJsonContext.Default.ClassificationResultPageDto, cancellationToken);

    // TODO: EnrichmentResultPageDto and MailIntelligenceStageIds.Enrichment with Contracts 3.0.0-alpha.1.
    public Task<MailIntelligenceResultPage<SummaryResultPageDto>> ReadEnrichmentAsync(
        MailIntelligenceJobState job, int page, CancellationToken cancellationToken)
        => ReadAsync(job, MailIntelligenceStageIdsV3.Enrichment, page,
            WinoAccountApiJsonContext.Default.SummaryResultPageDto, cancellationToken);

    private async Task<MailIntelligenceResultPage<T>> ReadAsync<T>(
        MailIntelligenceJobState job,
        string stage,
        int page,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken) where T : class
    {
        var content = await apiClient
            .GetMailIntelligenceResultPageAsync(job.MailboxId, job.JobId, stage, page, cancellationToken)
            .ConfigureAwait(false);

        if (job.ResultKeyId is null)
        {
            return new(Deserialize(content, typeInfo, stage), EnvelopeHash: null);
        }

        EncryptedResultPageDto? encrypted;
        try
        {
            encrypted = JsonSerializer.Deserialize(content, WinoAccountApiJsonContext.Default.EncryptedResultPageDto);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"The {stage} result page for an encrypted job was not encrypted and was refused.", exception);
        }

        if (encrypted is not { Envelope.Length: > 0 } ||
            !string.Equals(encrypted.Stage, stage, StringComparison.Ordinal) ||
            encrypted.PageIndex != page ||
            !string.Equals(encrypted.ResultKeyId, job.ResultKeyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The {stage} result page {page} does not belong to this job and was refused.");
        }

        var plaintext = await keys.DecryptAsync(
            job.ResultKeyId,
            encrypted.Envelope,
            job.MailboxId,
            MailIntelligenceResultRoutes.Page(job.MailboxId, job.JobId, stage, page),
            cancellationToken).ConfigureAwait(false);
        try
        {
            return new(Deserialize(plaintext, typeInfo, stage), MailIntelligenceResultDigest.PageHash(encrypted.Envelope));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static T Deserialize<T>(byte[] json, JsonTypeInfo<T> typeInfo, string stage) where T : class
        => JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new InvalidOperationException($"The {stage} result page was empty.");
}

/// <summary>
/// One opened result page. <paramref name="EnvelopeHash"/> is set for encrypted pages and feeds
/// the stage digest the client checks before acknowledging.
/// </summary>
public sealed record MailIntelligenceResultPage<T>(T Page, string? EnvelopeHash) where T : class;

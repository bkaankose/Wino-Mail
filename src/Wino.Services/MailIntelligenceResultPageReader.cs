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
/// Downloads one result page and opens it with the job's device result key. Plain JSON is
/// accepted only for a job that was never bound to a key; a keyed job whose page arrives
/// unencrypted is refused rather than trusted.
/// </summary>
public sealed class MailIntelligenceResultPageReader(
    IWinoAccountApiClient apiClient,
    IIntelligenceResultKeyStore keys)
{
    /// <summary>
    /// Route bound into the result page's authenticated data. Must match the server.
    /// </summary>
    public static string ResultRoute(Guid mailboxId, Guid jobId, string stage, int page)
        => $"/api/v2/ai/intelligence/mailboxes/{mailboxId:D}/jobs/{jobId:D}/results/{stage}/page-{page}";

    public Task<ClassificationResultPageDto> ReadClassificationAsync(
        MailIntelligenceJobState job, int page, CancellationToken cancellationToken)
        => ReadAsync(job, MailIntelligenceStageIds.Classification, page,
            WinoAccountApiJsonContext.Default.ClassificationResultPageDto, cancellationToken);

    // TODO: EnrichmentResultPageDto and MailIntelligenceStageIds.Enrichment with Contracts 3.0.0-alpha.1.
    public Task<SummaryResultPageDto> ReadEnrichmentAsync(
        MailIntelligenceJobState job, int page, CancellationToken cancellationToken)
        => ReadAsync(job, MailIntelligenceStageIds.Summarization, page,
            WinoAccountApiJsonContext.Default.SummaryResultPageDto, cancellationToken);

    private async Task<T> ReadAsync<T>(
        MailIntelligenceJobState job,
        string stage,
        int page,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken) where T : class
    {
        var payload = await apiClient
            .GetMailIntelligenceResultPageAsync(job.MailboxId, job.JobId, stage, page, cancellationToken)
            .ConfigureAwait(false);

        if (!payload.IsEncrypted)
        {
            if (job.ResultKeyId is not null)
            {
                throw new InvalidOperationException(
                    $"The {stage} result page for an encrypted job arrived unencrypted and was refused.");
            }

            return Deserialize(payload.Content, typeInfo, stage);
        }

        if (job.ResultKeyId is null)
        {
            throw new InvalidOperationException($"The {stage} result page is encrypted but the job has no result key.");
        }

        var plaintext = await keys.DecryptAsync(
            job.ResultKeyId,
            payload.Content,
            job.MailboxId,
            ResultRoute(job.MailboxId, job.JobId, stage, page),
            cancellationToken).ConfigureAwait(false);
        try
        {
            return Deserialize(plaintext, typeInfo, stage);
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

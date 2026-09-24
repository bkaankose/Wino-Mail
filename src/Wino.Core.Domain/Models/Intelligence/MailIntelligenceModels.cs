#nullable enable
using System;
using System.Collections.Generic;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

public enum MailIntelligenceStageKind
{
    Classification,
    Enrichment,
}

/// <summary>Identity of one artifact. The hash is the freshness key.</summary>
public sealed record MailArtifactKey(string RemoteMessageId, string ContentHash);

/// <param name="Hints">
/// Smart action kinds Classification expects Enrichment to extract, as their wire ids
/// (for example "oneTimeCode").
/// </param>
public sealed record ClassificationArtifact(
    MailArtifactKey Key,
    IReadOnlyList<string> Labels,
    string Priority,
    IReadOnlyList<string> Hints,
    bool IncludeInBriefing,
    DateTime CompletedUtc);

/// <param name="Headline">Briefing headline; empty for mail that is not in the briefing.</param>
/// <param name="Summary">Briefing summary; empty for mail that is not in the briefing.</param>
/// <param name="Actions">Typed smart actions Enrichment extracted, in the order it returned them.</param>
public sealed record EnrichmentArtifact(
    MailArtifactKey Key,
    string Headline,
    string Summary,
    IReadOnlyList<MailSmartAction> Actions,
    DateTime CompletedUtc);

public sealed record MailIntelligenceItemFailure(
    MailArtifactKey Key,
    MailIntelligenceStageKind Stage,
    string ErrorCode);

/// <summary>Outcome of importing one result page.</summary>
public sealed record MailIntelligenceImportResult(
    int Imported,
    int SkippedStale,
    int Failures);

/// <summary>A job this device submitted and is still tracking.</summary>
public sealed record MailIntelligenceJobState(
    Guid JobId,
    Guid LocalAccountId,
    Guid MailboxId,
    int MessageCount,
    string Status,
    MailIntelligenceStageState Classification,
    MailIntelligenceStageState Enrichment,
    int FailedCount,
    string? LastError,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    public bool IsFinished => Classification.IsAcknowledged && Enrichment.IsAcknowledged;

    /// <summary>The device result key the job's results are encrypted to; null for a legacy job.</summary>
    public string? ResultKeyId { get; init; }
}

public sealed record MailIntelligenceStageState(
    string Status,
    int PageCount,
    string? Digest,
    bool IsImported,
    bool IsAcknowledged);

/// <summary>One card in the briefing. Only Classification-included messages become cards.</summary>
public sealed record BriefingCard(
    Guid LocalAccountId,
    Guid MailUniqueId,
    string RemoteMessageId,
    string ContentHash,
    string Subject,
    string SenderName,
    string SenderAddress,
    DateTime ReceivedUtc,
    IReadOnlyList<string> Labels,
    string Priority,
    string Action,
    string? Headline,
    string? Summary,
    DateTime FirstImportedUtc,
    bool IsIgnored)
{
    /// <summary>
    /// A card counts as new when it first arrived after the briefing was last viewed.
    /// This replaces the artifact-revision comparison the old feed used.
    /// </summary>
    public bool IsNewSince(DateTime? lastViewedUtc)
        => lastViewedUtc is null || FirstImportedUtc > lastViewedUtc.Value;
}

/// <summary>Cards for one received day, newest day first.</summary>
public sealed record BriefingDayGroup(DateTime LocalDate, IReadOnlyList<BriefingCard> Cards);

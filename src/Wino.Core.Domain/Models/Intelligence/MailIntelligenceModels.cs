#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Intelligence;

public enum MailIntelligenceStageKind
{
    Classification,
    Enrichment,
}

/// <summary>Identity of one artifact. The hash is the freshness key.</summary>
public sealed record MailArtifactKey(string RemoteMessageId, string ContentHash);

/// <summary>
/// The raw judgments behind a classification, before any threshold was applied. Kept on
/// the device so a threshold change can be applied to results that are already here,
/// rather than by re-submitting the mailbox and paying for it a second time.
/// Keys are the lowercase label and priority names the rest of the app uses.
/// </summary>
public sealed record ClassificationSignals(
    IReadOnlyDictionary<string, double> LabelProbabilities,
    double BriefingProbability,
    double PriorityScore,
    IReadOnlyDictionary<string, double> PriorityProbabilities,
    string TopAction,
    double TopActionProbability,
    double ActionConfidence)
{
    public static ClassificationSignals Empty { get; } = new(
        new Dictionary<string, double>(),
        0d,
        0d,
        new Dictionary<string, double>(),
        "none",
        0d,
        0d);
}

public sealed record ClassificationArtifact(
    MailArtifactKey Key,
    IReadOnlyList<string> Labels,
    string Priority,
    string Action,
    bool IncludeInBriefing,
    DateTime CompletedUtc,
    ClassificationSignals Signals);

public sealed record EnrichmentArtifact(
    MailArtifactKey Key,
    string Headline,
    string Summary,
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

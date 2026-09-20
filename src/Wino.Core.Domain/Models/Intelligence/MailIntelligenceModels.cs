#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Intelligence;

public enum MailIntelligenceStageKind
{
    Jev,
    Luna,
}

/// <summary>Identity of one artifact. The hash is the freshness key.</summary>
public sealed record MailArtifactKey(string RemoteMessageId, string ContentHash);

public sealed record JevArtifact(
    MailArtifactKey Key,
    IReadOnlyList<string> Labels,
    string Priority,
    bool IncludeInBriefing,
    DateTime CompletedUtc);

public sealed record LunaArtifact(
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
    MailIntelligenceStageState Jev,
    MailIntelligenceStageState Luna,
    int FailedCount,
    string? LastError,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    public bool IsFinished => Jev.IsAcknowledged && Luna.IsAcknowledged;
}

public sealed record MailIntelligenceStageState(
    string Status,
    int PageCount,
    string? Digest,
    bool IsImported,
    bool IsAcknowledged);

/// <summary>One card in the briefing. Only Jev-included messages become cards.</summary>
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

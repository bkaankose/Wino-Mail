#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

public sealed record WinoIntelligenceContext(
    string ContentKey,
    Guid LocalAccountId,
    Guid MailUniqueId,
    Guid FileId,
    string MessageId,
    string AccountAddress,
    MailProviderType ProviderType,
    bool IsSemanticIndexingEnabled,
    string Subject,
    string Sender,
    DateTimeOffset OccurredAtUtc,
    string Html,
    MailContentProjection? InferenceProjection = null,
    MailContentProjection? TranslationProjection = null);

public sealed record WinoIntelligenceDeadline(
    DeadlineAction Action,
    DateTimeOffset? DueAtUtc,
    DateOnly? LocalDate,
    DateOnly? LocalDateEnd,
    string TimeZoneId,
    DeadlinePrecision Precision,
    double Confidence)
{
    public string ActionText => Action.ToString();
}

public sealed record WinoIntelligenceSnapshot(
    bool IsVisible,
    bool IsSummaryAvailable,
    bool IsTranslateAvailable,
    bool IsProcessingAvailable,
    bool IsSuggestedRepliesAvailable,
    bool IsFindSimilarAvailable,
    SemanticMessageIndexState ProcessingState,
    Guid? MailboxId,
    string? RemoteMessageId,
    bool NeedsReply,
    string NeedsReplyDetail,
    WinoIntelligenceDeadline? Deadline,
    string? CachedSummary)
{
    public static WinoIntelligenceSnapshot Hidden { get; } = new(
        false, false, false, false, false, false,
        SemanticMessageIndexState.Unsupported, null, null, false, string.Empty, null, null);
}

public sealed record WinoIntelligenceOperationResult<T>(
    Guid RequestId,
    string ContentKey,
    T? Value,
    bool IsCanceled,
    string? Error)
{
    public bool IsSuccess => !IsCanceled && Error is null;
}

public sealed record WinoSimilarMailItem(
    Guid MailUniqueId,
    string Subject,
    string Sender,
    DateTimeOffset OccurredAtUtc,
    double Similarity);

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
    MailContentProjection? TranslationProjection = null,
    MailIntelligenceMetadata? IntelligenceMetadata = null);

public sealed record WinoIntelligenceSnapshot(
    bool IsVisible,
    bool IsSummaryAvailable,
    bool IsTranslateAvailable,
    bool IsProcessingAvailable,
    MailMessageIntelligenceState ProcessingState,
    Guid? MailboxId,
    string? RemoteMessageId,
    MailIntelligenceMetadata? Metadata,
    string? CachedSummary)
{
    /// <summary>
    /// Rewrite shares the summarize and translate gate: AI Pack, current consent and quota that
    /// can still be consumed. The API enforces the same three checks for its rewrite endpoint.
    /// </summary>
    public bool IsRewriteAvailable => IsSummaryAvailable;

    public static WinoIntelligenceSnapshot Hidden { get; } = new(
        false, false, false, false,
        MailMessageIntelligenceState.Unsupported, null, null, null, null);
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

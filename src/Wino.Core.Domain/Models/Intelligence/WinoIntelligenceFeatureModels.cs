#nullable enable
using System;
using System.Collections.Generic;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

public sealed record WinoSemanticSearchItem(
    Guid MailboxId,
    string RemoteMessageId,
    double Similarity);

public sealed record WinoSuggestedReplyMessage(
    string RemoteMessageId,
    string ContentHash,
    string Subject,
    string Sender,
    string Body,
    DateTimeOffset OccurredAtUtc,
    bool IsOutgoing);

public sealed record WinoSuggestedRepliesRequest(
    WinoSuggestedReplyMessage Target,
    IReadOnlyList<WinoSuggestedReplyMessage> Thread,
    IReadOnlyList<WinoSuggestedReplyMessage> CandidateExamples,
    string Tone = "neutral",
    int Count = 3);

public sealed record WinoSuggestedRepliesResult(
    Guid RequestId,
    string Language,
    IReadOnlyList<WinoSuggestedReply> Suggestions,
    IReadOnlyList<string> RetrievedContextMessageIds,
    string GenerationProfileId);

public sealed record WinoSuggestedReply(string Text, string Tone);

public sealed record WinoDeadlineMetadata(
    string RemoteMessageId,
    string ContentHash,
    DeadlineKind DeadlineKind,
    DateTimeOffset? DueAtUtc,
    DateOnly? LocalDate,
    string TimeZoneId,
    DeadlinePrecision Precision,
    DeadlineAction Action,
    string Status,
    double Confidence,
    DateTimeOffset GeneratedAtUtc);

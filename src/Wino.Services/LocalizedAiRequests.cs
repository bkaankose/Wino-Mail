#nullable enable
using System;
using System.Collections.Generic;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

internal sealed record LocalizedRewriteRequest(
    string Html,
    string Mode,
    string Language);

internal sealed record LocalizedDailyBriefingRequest(
    DateOnly LocalDate,
    string TimeZoneId,
    string Language,
    bool ForceRegenerate);

internal sealed record LocalizedSuggestedRepliesRequest(
    Wino.Core.Domain.Models.Intelligence.WinoSuggestedReplyMessage Target,
    IReadOnlyList<Wino.Core.Domain.Models.Intelligence.WinoSuggestedReplyMessage> Thread,
    IReadOnlyList<Wino.Core.Domain.Models.Intelligence.WinoSuggestedReplyMessage> CandidateExamples,
    string Language,
    string Tone,
    int Count);

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

public sealed record MailIntelligenceMetadata(
    string RemoteMessageId,
    IReadOnlyList<SmartLabelScore> SmartLabels,
    BriefingFactCapabilityPayload? BriefingFact,
    string Headline)
{
    public NeedsReplyCapabilityPayload? NeedsReply => BriefingFact?.PrimaryAction is ReplyActionPayload action
        ? new(true, action.Confidence)
        : null;

    public PriorityCapabilityPayload? Priority => BriefingFact is null
        ? null
        : new(BriefingFact.Urgency, BriefingFact.Confidence);

    public string VerificationCode => (BriefingFact?.PrimaryAction as CopyVerificationCodeActionPayload)?.Code ?? string.Empty;

    public DeadlineCapabilityPayload? Deadline
    {
        get
        {
            var deadline = BriefingFact?.TemporalReferences.OfType<DeadlineTemporalPayload>().FirstOrDefault();
            if (deadline is null) return null;
            var point = deadline.Due;
            return new DeadlineCapabilityPayload(
                true,
                DeadlineKind.Other,
                point.InstantUtc,
                point.LocalDate,
                point.TimeZoneId,
                point.Precision switch
                {
                    TemporalPrecision.ExactDateTime => DeadlinePrecision.DateTime,
                    TemporalPrecision.Date => DeadlinePrecision.Date,
                    TemporalPrecision.Month => DeadlinePrecision.Month,
                    _ => DeadlinePrecision.Unknown,
                },
                DeadlineAction.None,
                deadline.Confidence,
                point.LocalDate);
        }
    }

    public bool HasVisibleMetadata => SmartLabels.Count > 0 || BriefingFact is not null || !string.IsNullOrWhiteSpace(Headline);
}

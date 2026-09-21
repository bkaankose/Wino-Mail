#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// What this device knows about one message after processing.
/// Classification supplies the labels, the priority and the briefing decision; Summarization supplies the
/// headline and summary, and only for messages Classification included. There is deliberately no
/// deadline, due date, action or status here: those were the least reliable outputs of
/// the previous design and the decision model cannot produce them.
/// </summary>
public sealed record MailIntelligenceMetadata(
    string RemoteMessageId,
    IReadOnlyList<string> Labels,
    string Priority,
    bool IncludeInBriefing,
    string Headline = "",
    string Summary = "")
{
    public static MailIntelligenceMetadata Empty { get; } = new(string.Empty, [], "normal", false);

    public bool HasLabels => Labels.Count > 0;

    public bool HasHeadline => !string.IsNullOrWhiteSpace(Headline);

    public bool IsHighPriority =>
        string.Equals(Priority, "high", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Priority, "urgent", StringComparison.OrdinalIgnoreCase);

    public static MailIntelligenceMetadata From(ClassificationArtifact jev, SummaryArtifact? luna = null) => new(
        jev.Key.RemoteMessageId,
        jev.Labels,
        jev.Priority,
        jev.IncludeInBriefing,
        luna?.Headline ?? string.Empty,
        luna?.Summary ?? string.Empty);

    /// <summary>Labels remaining after the user's per-account indicator settings.</summary>
    public IReadOnlyList<string> VisibleLabels(IReadOnlySet<string>? enabledLabels)
        => enabledLabels is null
            ? Labels
            : [.. Labels.Where(enabledLabels.Contains)];
}

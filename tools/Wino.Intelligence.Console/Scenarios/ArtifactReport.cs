using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Intelligence.ConsoleApp.Hosting;

namespace Wino.Intelligence.ConsoleApp.Scenarios;

/// <summary>Reads imported artifacts back from the device store, the same rows the app renders.</summary>
internal static class ArtifactReport
{
    public static async Task PrintSummaryAsync(ScenarioContext context, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken)
    {
        var store = context.Get<IMailIntelligenceStore>();
        var accountId = context.Account.Id;
        var classifications = await store.GetClassificationArtifactsAsync(accountId, remoteMessageIds, cancellationToken).ConfigureAwait(false);
        var enrichments = await store.GetEnrichmentArtifactsAsync(accountId, remoteMessageIds, cancellationToken).ConfigureAwait(false);

        ConsoleOutput.Header("Results in the device store");
        ConsoleOutput.KeyValue("Classified", $"{classifications.Count}/{remoteMessageIds.Count}");
        ConsoleOutput.KeyValue("Included in briefing", classifications.Values.Count(static artifact => artifact.IncludeInBriefing));
        ConsoleOutput.KeyValue("Enriched", enrichments.Count);
        ConsoleOutput.KeyValue("With a headline", enrichments.Values.Count(static artifact => !string.IsNullOrWhiteSpace(artifact.Headline)));
        ConsoleOutput.KeyValue("With smart actions", enrichments.Values.Count(static artifact => artifact.Actions.Count > 0));

        if (classifications.Count == 0)
            return;

        ConsoleOutput.KeyValue("Priority", string.Join(", ", classifications.Values
            .GroupBy(static artifact => artifact.Priority)
            .OrderByDescending(static group => group.Count())
            .Select(static group => $"{group.Key} {group.Count()}")));
        ConsoleOutput.KeyValue("Labels", string.Join(", ", classifications.Values
            .SelectMany(static artifact => artifact.Labels)
            .GroupBy(static label => label, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .Select(static group => $"{group.Key} {group.Count()}")));
        ConsoleOutput.KeyValue("Hints", string.Join(", ", classifications.Values
            .SelectMany(static artifact => artifact.Hints)
            .GroupBy(static hint => hint, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .Select(static group => $"{group.Key} {group.Count()}")) is { Length: > 0 } hints ? hints : "-");

        var missing = remoteMessageIds.Count - classifications.Count;
        if (missing > 0)
            ConsoleOutput.Warning($"  {missing} selected message(s) have no classification yet (still running, failed, or skipped as stale).");
    }

    public static async Task PrintMessagesAsync(ScenarioContext context, IReadOnlyList<IntelligenceMessageCandidate> candidates, CancellationToken cancellationToken)
    {
        var store = context.Get<IMailIntelligenceStore>();
        var ids = candidates.Select(static candidate => candidate.RemoteMessageId).ToArray();
        var classifications = await store.GetClassificationArtifactsAsync(context.Account.Id, ids, cancellationToken).ConfigureAwait(false);
        var enrichments = await store.GetEnrichmentArtifactsAsync(context.Account.Id, ids, cancellationToken).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            ConsoleOutput.Header($"{candidate.ReceivedAt.ToLocalTime():g}  {candidate.SenderName} <{candidate.Sender}>");
            ConsoleOutput.Info($"  {candidate.Subject}");

            if (!classifications.TryGetValue(candidate.RemoteMessageId, out var classification))
            {
                ConsoleOutput.Muted("  not classified");
                continue;
            }

            ConsoleOutput.KeyValue("Priority", classification.Priority, 18);
            ConsoleOutput.KeyValue("Labels", string.Join(", ", classification.Labels), 18);
            ConsoleOutput.KeyValue("Hints", classification.Hints.Count == 0 ? "-" : string.Join(", ", classification.Hints), 18);
            ConsoleOutput.KeyValue("In briefing", classification.IncludeInBriefing, 18);
            ConsoleOutput.KeyValue("Content hash", classification.Key.ContentHash, 18);
            ConsoleOutput.KeyValue("Completed", classification.CompletedUtc.ToLocalTime().ToString("G"), 18);

            if (!enrichments.TryGetValue(candidate.RemoteMessageId, out var enrichment))
            {
                ConsoleOutput.Muted("  no enrichment (only briefing messages are enriched)");
                continue;
            }

            ConsoleOutput.KeyValue("Headline", enrichment.Headline, 18);
            ConsoleOutput.KeyValue("Summary", enrichment.Summary, 18);
            foreach (var action in enrichment.Actions)
                ConsoleOutput.KeyValue("Action", action, 18);
        }
    }
}

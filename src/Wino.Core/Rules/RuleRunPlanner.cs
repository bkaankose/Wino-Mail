using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Rules;

namespace Wino.Core.Rules;

/// <summary>The evaluable facts of a single message, used to run rules client-side ("Run rules now").</summary>
public sealed class RuleMessageFacts
{
    public Guid MessageId { get; init; }
    public string FromAddress { get; init; }

    /// <summary>The sender's display name, so a From condition stored as a name or legacy EX DN still matches.</summary>
    public string FromName { get; init; }

    public string Subject { get; init; }

    /// <summary>"High" / "Normal" / "Low" (matches the importance condition value).</summary>
    public string Importance { get; init; }

    public bool HasAttachments { get; init; }

    /// <summary>Recipients (To/Cc) for the Sent-to condition; may be empty if not loaded.</summary>
    public IReadOnlyList<string> Recipients { get; init; } = Array.Empty<string>();

    /// <summary>Body text for the Body-contains condition; may be null if not loaded.</summary>
    public string Body { get; init; }
}

/// <summary>A coalesced action to apply to a set of messages (executable subset only).</summary>
public sealed class RuleRunBatch
{
    public RuleActionType Action { get; }
    public string Value { get; }
    public IReadOnlyList<Guid> MessageIds { get; }

    public RuleRunBatch(RuleActionType action, string value, IReadOnlyList<Guid> messageIds)
    {
        Action = action;
        Value = value;
        MessageIds = messageIds;
    }
}

public sealed class RuleRunPlan
{
    public IReadOnlyList<RuleRunBatch> Batches { get; }

    /// <summary>Number of copy/forward action applications skipped (no client-side execution path).</summary>
    public int SkippedCopyForwardCount { get; }

    /// <summary>Distinct messages that have at least one executable action.</summary>
    public int MatchedMessageCount { get; }

    public RuleRunPlan(IReadOnlyList<RuleRunBatch> batches, int skippedCopyForwardCount, int matchedMessageCount)
    {
        Batches = batches;
        SkippedCopyForwardCount = skippedCopyForwardCount;
        MatchedMessageCount = matchedMessageCount;
    }

    public bool IsEmpty => Batches.Count == 0 && SkippedCopyForwardCount == 0;
}

/// <summary>
/// Pure evaluator for "Run rules now": maps (enabled rules, message facts) to a coalesced set of
/// action batches, honoring priority order and stop-processing. Copy and Forward have no client-side
/// execution path, so they are counted as skipped rather than batched (they still run server-side on
/// incoming mail). Side-effect free and unit-tested.
/// </summary>
public static class RuleRunPlanner
{
    public static RuleRunPlan Plan(IReadOnlyList<RemoteInboxRule> rules, IReadOnlyList<RuleMessageFacts> messages)
    {
        // Read-only rules are excluded: the fidelity guard marks them read-only precisely because parts of
        // their conditions could not be mapped, and evaluating the lossy remainder over-matches. A rule
        // whose only condition was unmappable arrives with an empty condition list, which would otherwise
        // match every message (Conditions.All on an empty list is true) and mass-move the folder.
        var ordered = (rules ?? Array.Empty<RemoteInboxRule>())
            .Where(r => r.IsEnabled && !r.IsReadOnly)
            .OrderBy(r => r.Priority)
            .ToList();

        // Preserve insertion order of (action,value) groups for stable output.
        var keys = new List<(RuleActionType Action, string Value)>();
        var groups = new Dictionary<(RuleActionType, string), List<Guid>>();
        var matched = new HashSet<Guid>();
        var skipped = 0;

        foreach (var message in messages ?? Array.Empty<RuleMessageFacts>())
        {
            foreach (var rule in ordered)
            {
                if (!RuleMatches(rule, message))
                    continue;

                foreach (var action in rule.Actions)
                {
                    if (action.Type is RuleActionType.Copy or RuleActionType.Forward)
                    {
                        skipped++;
                        continue;
                    }

                    var key = (action.Type, action.Value ?? string.Empty);
                    if (!groups.TryGetValue(key, out var ids))
                    {
                        groups[key] = ids = new List<Guid>();
                        keys.Add(key);
                    }

                    if (!ids.Contains(message.MessageId))
                        ids.Add(message.MessageId);

                    matched.Add(message.MessageId);
                }

                if (rule.StopProcessing)
                    break;
            }
        }

        var batches = keys.Select(k => new RuleRunBatch(k.Action, k.Value, groups[k])).ToList();
        return new RuleRunPlan(batches, skipped, matched.Count);
    }

    private static bool RuleMatches(RemoteInboxRule rule, RuleMessageFacts message)
        => rule.Conditions.All(condition => ConditionMatches(condition, message));

    private static bool ConditionMatches(RuleConditionModel condition, RuleMessageFacts message) => condition.Field switch
    {
        RuleConditionField.From => SenderMatches(message.FromAddress, message.FromName, condition.Value),
        RuleConditionField.SubjectContains => Contains(message.Subject, condition.Value),
        RuleConditionField.BodyContains => Contains(message.Body, condition.Value),
        RuleConditionField.SentTo => RecipientsContainAny(message.Recipients, condition.Value),
        RuleConditionField.Importance => string.Equals(message.Importance, condition.Value, StringComparison.OrdinalIgnoreCase),
        RuleConditionField.HasAttachment => message.HasAttachments,
        _ => false
    };

    // An empty condition value is no constraint (matches), mirroring the server dropping empty predicates.
    private static bool Contains(string source, string value)
        => string.IsNullOrEmpty(value) || (source != null && source.Contains(value, StringComparison.OrdinalIgnoreCase));

    // A From condition's value may be an SMTP address, a display name, or a legacy Exchange X500/EX DN
    // (which embeds the display name, e.g. ".../cn=...-Matthew Johnson"). The message carries an SMTP
    // address + a display name, so match a value part against the address OR the name in either direction.
    private static bool SenderMatches(string address, string name, string commaSeparatedValues)
    {
        var parts = SplitValues(commaSeparatedValues);
        if (parts.Count == 0)
            return true;

        foreach (var part in parts)
        {
            if (address != null && address.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(name) &&
                (name.Contains(part, StringComparison.OrdinalIgnoreCase) || part.Contains(name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static bool RecipientsContainAny(IReadOnlyList<string> recipients, string commaSeparatedValues)
    {
        var parts = SplitValues(commaSeparatedValues);
        if (parts.Count == 0)
            return true;

        return recipients != null && recipients.Any(r => r != null && parts.Any(p => r.Contains(p, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> SplitValues(string value)
        => string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(p => p.Trim())
                   .Where(p => p.Length > 0)
                   .ToList();
}

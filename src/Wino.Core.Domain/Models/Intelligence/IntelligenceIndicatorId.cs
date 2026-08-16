#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>Identifies one of the fact-derived intelligence indicators.</summary>
public enum IntelligenceFactKind
{
    Deadline,
    NeedsReply,
    Priority,
    Briefing,
}

/// <summary>Identifies the namespace used by a stable intelligence indicator id.</summary>
public enum IntelligenceIndicatorKind
{
    Fact,
    SmartLabel,
}

/// <summary>
/// Stable, mailbox-independent identifier for a visible intelligence indicator.
/// IDs are intentionally based on contract names instead of enum ordinals so that
/// adding a contract value does not change existing local preferences.
/// </summary>
public readonly record struct IntelligenceIndicatorId
{
    public const string FactDeadline = "fact:deadline";
    public const string FactNeedsReply = "fact:needs-reply";
    public const string FactPriority = "fact:priority";
    public const string FactBriefing = "fact:briefing";

    public const string Deadline = FactDeadline;
    public const string NeedsReply = FactNeedsReply;
    public const string Priority = FactPriority;
    public const string Briefing = FactBriefing;

    public const string FactPrefix = "fact:";
    public const string SmartLabelPrefix = "label:";

    public IntelligenceIndicatorId(string value)
    {
        Value = value ?? string.Empty;
    }

    public string Value { get; }

    public IntelligenceIndicatorKind Kind => Value.StartsWith(FactPrefix, StringComparison.Ordinal)
        ? IntelligenceIndicatorKind.Fact
        : IntelligenceIndicatorKind.SmartLabel;

    public bool IsFact => TryGetFactKind(out _);

    public bool IsSmartLabel => TryGetSmartLabel(out _);

    public bool TryGetFactKind(out IntelligenceFactKind kind)
    {
        kind = Value switch
        {
            FactDeadline => IntelligenceFactKind.Deadline,
            FactNeedsReply => IntelligenceFactKind.NeedsReply,
            FactPriority => IntelligenceFactKind.Priority,
            FactBriefing => IntelligenceFactKind.Briefing,
            _ => default,
        };

        return Value is FactDeadline or FactNeedsReply or FactPriority or FactBriefing;
    }

    public bool TryGetSmartLabel(out MailSmartLabel label)
    {
        label = default;
        if (!Value.StartsWith(SmartLabelPrefix, StringComparison.Ordinal))
            return false;

        var name = Value[SmartLabelPrefix.Length..];
        if (!Enum.TryParse<MailSmartLabel>(name, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            return false;
        }

        label = parsed;
        return string.Equals(Value, ForSmartLabel(parsed).Value, StringComparison.Ordinal);
    }

    public override string ToString() => Value ?? string.Empty;

    public static IntelligenceIndicatorId ForFact(IntelligenceFactKind kind) => kind switch
    {
        IntelligenceFactKind.Deadline => new(FactDeadline),
        IntelligenceFactKind.NeedsReply => new(FactNeedsReply),
        IntelligenceFactKind.Priority => new(FactPriority),
        IntelligenceFactKind.Briefing => new(FactBriefing),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static IntelligenceIndicatorId ForSmartLabel(MailSmartLabel label)
    {
        if (!Enum.IsDefined(label))
            throw new ArgumentOutOfRangeException(nameof(label), label, null);

        return new($"{SmartLabelPrefix}{label.ToString().ToLowerInvariant()}");
    }

    public static bool TryParse(string? value, out IntelligenceIndicatorId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (candidate.Equals(FactDeadline, StringComparison.Ordinal) ||
            candidate.Equals(FactNeedsReply, StringComparison.Ordinal) ||
            candidate.Equals(FactPriority, StringComparison.Ordinal) ||
            candidate.Equals(FactBriefing, StringComparison.Ordinal))
        {
            id = new(candidate);
            return true;
        }

        if (!candidate.StartsWith(SmartLabelPrefix, StringComparison.Ordinal))
            return false;

        var name = candidate[SmartLabelPrefix.Length..];
        if (!Enum.TryParse<MailSmartLabel>(name, ignoreCase: true, out var label) ||
            !Enum.IsDefined(label))
        {
            return false;
        }

        id = ForSmartLabel(label);
        return true;
    }

    public static IntelligenceIndicatorId Parse(string value)
        => TryParse(value, out var id)
            ? id
            : throw new FormatException($"'{value}' is not a known intelligence indicator id.");

    public static IReadOnlyList<IntelligenceIndicatorId> GetKnownIndicators()
        => [
            new(FactDeadline),
            new(FactNeedsReply),
            new(FactPriority),
            new(FactBriefing),
            .. Enum.GetValues<MailSmartLabel>().Select(ForSmartLabel),
        ];

    /// <summary>
    /// Normalizes persisted ids and drops malformed or obsolete values. This is also
    /// deliberately forward-compatible: a label added to the contracts assembly is
    /// automatically accepted when this client understands that enum value.
    /// </summary>
    public static HashSet<string> NormalizeExcluded(IEnumerable<string>? values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (values is null)
            return result;

        foreach (var value in values)
        {
            if (TryParse(value, out var id))
                result.Add(id.Value);
        }

        return result;
    }

    public static HashSet<string> ParsePersisted(string? persisted)
    {
        if (string.IsNullOrWhiteSpace(persisted))
            return new(StringComparer.Ordinal);

        return NormalizeExcluded(persisted.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries));
    }

    public static string SerializePersisted(IEnumerable<string>? values)
        => string.Join('\n', NormalizeExcluded(values).OrderBy(static value => value, StringComparer.Ordinal));
}

/// <summary>Shared visibility decisions for all intelligence presentation surfaces.</summary>
public static class IntelligenceVisibilityPolicy
{
    public static bool IsVisible(MailAccountPreferences? preferences, IntelligenceIndicatorId indicator)
        => preferences is null || IsVisible(preferences.ExcludedIntelligenceIndicatorIds, indicator);

    public static bool IsVisible(IReadOnlySet<string>? excludedIds, IntelligenceIndicatorId indicator)
    {
        if (excludedIds is null || excludedIds.Count == 0)
            return true;

        if (excludedIds.Contains(indicator.Value))
            return false;

        return !excludedIds.Any(value =>
            IntelligenceIndicatorId.TryParse(value, out var parsed) &&
            parsed.Value == indicator.Value);
    }

    public static bool IsVisible(MailAccountPreferences? preferences, IntelligenceFactKind kind)
        => IsVisible(preferences, IntelligenceIndicatorId.ForFact(kind));

    public static bool IsVisible(IReadOnlySet<string>? excludedIds, IntelligenceFactKind kind)
        => IsVisible(excludedIds, IntelligenceIndicatorId.ForFact(kind));

    public static bool IsVisible(MailAccountPreferences? preferences, MailSmartLabel label)
        => IsVisible(preferences, IntelligenceIndicatorId.ForSmartLabel(label));

    public static bool IsVisible(IReadOnlySet<string>? excludedIds, MailSmartLabel label)
        => IsVisible(excludedIds, IntelligenceIndicatorId.ForSmartLabel(label));

    public static IReadOnlySet<string> NormalizeExcluded(IEnumerable<string>? values)
        => IntelligenceIndicatorId.NormalizeExcluded(values);
}

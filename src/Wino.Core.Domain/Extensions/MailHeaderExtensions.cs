using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Extensions;

public static class MailHeaderExtensions
{
    public static string NormalizeMessageId(string value)
    {
        if (value == null)
            return null;

        var normalized = StripAngleBrackets(value)?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
    }

    public static string ToHeaderMessageId(string value)
    {
        var normalized = NormalizeMessageId(value);
        return string.IsNullOrEmpty(normalized) ? string.Empty : $"<{normalized}>";
    }

    /// <summary>
    /// Strips angle brackets from a Message-ID or In-Reply-To value.
    /// RFC 5322 Message-IDs are formatted as &lt;id@domain&gt;, but MimeKit
    /// properties store them without brackets. This normalizes raw header
    /// values to match MimeKit's convention.
    /// </summary>
    public static string StripAngleBrackets(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        value = value.Trim();

        if (value.StartsWith("<") && value.EndsWith(">"))
            return value.Substring(1, value.Length - 2);

        return value;
    }

    /// <summary>
    /// Normalizes a raw RFC References header value into semicolon-separated Message-IDs
    /// without angle brackets. Raw References headers contain space-separated bracketed IDs
    /// like "&lt;id1@domain&gt; &lt;id2@domain&gt;". This converts them to "id1@domain;id2@domain".
    /// </summary>
    public static string NormalizeReferences(string rawReferences)
        => JoinStoredReferences(SplitMessageIds(rawReferences));

    public static IEnumerable<string> SplitMessageIds(string values)
    {
        if (string.IsNullOrWhiteSpace(values))
            return [];

        return values
            .Split(new[] { ' ', '\t', '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeMessageId)
            .Where(id => !string.IsNullOrEmpty(id));
    }

    public static string JoinStoredReferences(IEnumerable<string> values)
        => string.Join(";", NormalizeDistinctMessageIds(values));

    public static string BuildReferencesHeaderValue(IEnumerable<string> values)
        => string.Join(" ", NormalizeDistinctMessageIds(values).Select(ToHeaderMessageId));

    public static List<string> BuildReferencesChain(IEnumerable<string> existingReferences, string parentMessageId)
    {
        var results = NormalizeDistinctMessageIds(existingReferences).ToList();
        var normalizedParentMessageId = NormalizeMessageId(parentMessageId);

        if (!string.IsNullOrEmpty(normalizedParentMessageId) &&
            !results.Contains(normalizedParentMessageId, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(normalizedParentMessageId);
        }

        return results;
    }

    private static IEnumerable<string> NormalizeDistinctMessageIds(IEnumerable<string> values)
    {
        if (values == null)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            var normalized = NormalizeMessageId(value);
            if (string.IsNullOrEmpty(normalized) || !seen.Add(normalized))
                continue;

            yield return normalized;
        }
    }
}

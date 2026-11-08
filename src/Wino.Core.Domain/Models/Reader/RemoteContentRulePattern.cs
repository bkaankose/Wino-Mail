using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// The one place that turns user input or a sender address into a sender rule pattern.
/// </summary>
public static class RemoteContentRulePattern
{
    /// <summary>
    /// Trims, lower-cases and strips <c>mailto:</c>, angle brackets and a leading <c>@</c> or <c>*.</c>.
    /// Returns null when the input is neither an address nor a domain with a dot.
    /// </summary>
    public static string? Normalize(string? input, out RemoteContentRuleKind kind)
    {
        kind = RemoteContentRuleKind.Address;

        if (string.IsNullOrWhiteSpace(input))
            return null;

        var value = input.Trim().ToLowerInvariant();

        if (value.StartsWith('<') && value.EndsWith('>'))
            value = value[1..^1].Trim();

        if (value.StartsWith("mailto:", StringComparison.Ordinal))
            value = value["mailto:".Length..].Trim();

        if (value.StartsWith("*.", StringComparison.Ordinal))
            value = value[2..];
        else if (value.StartsWith('@'))
            value = value[1..];

        value = value.TrimEnd('.');

        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
            return null;

        var at = value.IndexOf('@');
        if (at >= 0)
        {
            if (at == 0 || at != value.LastIndexOf('@') || !IsValidDomain(value[(at + 1)..]))
                return null;

            kind = RemoteContentRuleKind.Address;
            return value;
        }

        if (!IsValidDomain(value))
            return null;

        kind = RemoteContentRuleKind.Domain;
        return value;
    }

    /// <summary>
    /// The lower-case domain of an address, or an empty string when it has none.
    /// </summary>
    public static string GetDomain(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return string.Empty;

        var trimmed = address.Trim();
        var at = trimmed.LastIndexOf('@');

        return at >= 0 && at < trimmed.Length - 1
            ? trimmed[(at + 1)..].TrimEnd('.').ToLowerInvariant()
            : string.Empty;
    }

    /// <summary>
    /// The domain and each parent that still has a dot, most specific first: <c>news.temu.com</c>, <c>temu.com</c>.
    /// </summary>
    public static List<string> GetDomainCandidates(string? domain)
    {
        var candidates = new List<string>();

        if (string.IsNullOrWhiteSpace(domain))
            return candidates;

        var current = domain.Trim().TrimEnd('.').ToLowerInvariant();

        while (current.Contains('.'))
        {
            candidates.Add(current);
            current = current[(current.IndexOf('.') + 1)..];
        }

        return candidates;
    }

    private static bool IsValidDomain(string domain)
        => domain.Length > 0
           && domain.Contains('.')
           && !domain.StartsWith('.')
           && !domain.Contains("..", StringComparison.Ordinal)
           && domain.All(c => char.IsLetterOrDigit(c) || c is '-' or '.');
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>
/// Decides whether a received sender is a person worth suggesting when composing.
/// The rules stay conservative: an automated address that slips through costs one extra
/// suggestion, while a real person filtered out silently disappears from the picker.
/// </summary>
public static class RecipientAddressHeuristics
{
    // Local parts that never belong to a person, whatever the display name says.
    private static readonly HashSet<string> HardLocalParts = new(StringComparer.Ordinal)
    {
        "mailerdaemon", "postmaster", "bounce", "bounces", "unsubscribe", "mailer",
        "newsletter", "newsletters", "marketing", "notification", "notifications",
        "updates", "digest", "alerts", "alert"
    };

    // Shared mailboxes that a small business may still answer as a person.
    private static readonly HashSet<string> SoftLocalParts = new(StringComparer.Ordinal)
    {
        "info", "support", "team", "hello", "news", "contact", "help", "service", "admin"
    };

    // First domain labels that bulk senders use for their delivery and bounce hosts.
    private static readonly HashSet<string> SendingSubdomains = new(StringComparer.Ordinal)
    {
        "bounce", "bounces", "bnc", "email", "em", "mail", "mailer", "mta", "smtp",
        "notify", "notifications", "noreply", "reply", "alerts", "news", "newsletter", "mkt", "marketing"
    };

    // Words that mark a display name as a service, not a person.
    private static readonly HashSet<string> ServiceNameWords = new(StringComparer.Ordinal)
    {
        "team", "support", "bot", "alerts", "alert", "notifications", "notification", "newsletter",
        "updates", "news", "info", "service", "services", "noreply", "digest", "marketing", "no-reply"
    };

    private const string Vowels = "aeiouy";

    /// <summary>
    /// True when a received sender looks automated or machine generated and should not be remembered.
    /// </summary>
    public static bool IsAutomatedAddress(string address, string displayName)
    {
        if (!TrySplit(address, out var local, out var domain))
            return true;

        var compact = RemoveSeparators(local, ".-_");

        if (compact.StartsWith("reply+", StringComparison.Ordinal) ||
            local.StartsWith("reply-", StringComparison.Ordinal) ||
            compact.Contains("noreply", StringComparison.Ordinal) ||
            compact.Contains("donotreply", StringComparison.Ordinal) ||
            HardLocalParts.Contains(compact))
            return true;

        if (HasTokenPlusTag(local))
            return true;

        var isHumanName = LooksHumanDisplayName(displayName, address);

        if (SoftLocalParts.Contains(compact))
            return !isHumanName;

        if (IsSendingSubdomain(domain) && !IsPlainNameLocalPart(local))
            return !isHumanName;

        if (LooksMachineGenerated(local))
            return !isHumanName;

        return false;
    }

    /// <summary>
    /// True for a local part that reads as a token rather than something a person chose,
    /// for example <c>a23asd21asdju12398asdf9nfg9hwe</c> or a hex identifier.
    /// Short local parts are never flagged.
    /// </summary>
    public static bool LooksMachineGenerated(string localPart)
    {
        if (string.IsNullOrWhiteSpace(localPart))
            return false;

        var s = RemoveSeparators(localPart.Trim().ToLowerInvariant(), ".-_+=");
        if (s.Length < 16)
            return false;

        var digits = s.Count(char.IsDigit);

        // Hex and GUID identifiers.
        if (digits > 0 && s.All(Uri.IsHexDigit))
            return true;

        if (digits / (double)s.Length >= 0.30)
            return true;

        var transitions = 0;
        for (var i = 1; i < s.Length; i++)
        {
            if (char.IsDigit(s[i]) != char.IsDigit(s[i - 1]))
                transitions++;
        }

        if (transitions >= 4)
            return true;

        var letters = s.Where(char.IsLetter).ToList();
        if (letters.Count >= 20 && letters.Count(c => Vowels.Contains(c)) / (double)letters.Count < 0.15)
            return true;

        return LongestConsonantRun(s) >= 8;
    }

    /// <summary>
    /// True for a display name that reads as a person's name: two to five words, no digits,
    /// no ticket or repository markers, and no service words such as "Team" or "Support".
    /// </summary>
    public static bool LooksHumanDisplayName(string displayName, string address = null)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Contains('@'))
            return false;

        if (address is not null && string.Equals(name, address.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.Any(char.IsDigit) || name.IndexOfAny(['#', '[', ']', '|', '<', '>', ':', ';']) >= 0)
            return false;

        var lower = name.ToLowerInvariant();
        if (lower.Contains("notification", StringComparison.Ordinal) ||
            lower.Contains("noreply", StringComparison.Ordinal) ||
            lower.Contains("do not reply", StringComparison.Ordinal) ||
            lower.Contains("newsletter", StringComparison.Ordinal))
            return false;

        var words = lower.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 5)
            return false;

        return words.All(word => char.IsLetter(word[0]) && !ServiceNameWords.Contains(word.Trim(',', '.', '(', ')')));
    }

    private static bool TrySplit(string address, out string local, out string domain)
    {
        local = domain = null;
        var trimmed = address?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1)
            return false;

        local = trimmed[..at].ToLowerInvariant();
        domain = trimmed[(at + 1)..].ToLowerInvariant();
        return true;
    }

    // "reply+AB12CD34" or "bounces+1234-abcd": a tag that carries a token, not a filter word like "+work".
    private static bool HasTokenPlusTag(string local)
    {
        var plus = local.IndexOf('+');
        if (plus < 0)
            return false;

        var tag = local[(plus + 1)..];
        return tag.Length >= 8 && tag.Any(char.IsDigit);
    }

    private static bool IsSendingSubdomain(string domain)
    {
        var labels = domain.Split('.');
        return labels.Length >= 3 && SendingSubdomains.Contains(labels[0]);
    }

    // Letters with at most two separators, such as "mike" or "anna.maria-smith".
    private static bool IsPlainNameLocalPart(string local)
    {
        var separators = 0;
        foreach (var c in local)
        {
            if (c is '.' or '-' or '_')
                separators++;
            else if (!char.IsLetter(c))
                return false;
        }

        return separators <= 2;
    }

    private static int LongestConsonantRun(string value)
    {
        int longest = 0, current = 0;
        foreach (var c in value)
        {
            current = char.IsLetter(c) && !Vowels.Contains(c) ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static string RemoveSeparators(string value, string separators)
        => string.Concat(value.Where(c => !separators.Contains(c)));
}

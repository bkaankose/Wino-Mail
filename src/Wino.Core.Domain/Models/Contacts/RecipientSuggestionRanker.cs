using System;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>
/// Orders compose suggestions: how well the text matches comes first, then how often and how
/// recently the account corresponded with the person, then whether they are a saved contact.
/// </summary>
public static class RecipientSuggestionRanker
{
    private const double RecencyHalfLifeDays = 90;

    public static double Score(
        string query,
        string displayName,
        string address,
        int sentCount,
        int receivedCount,
        DateTime? lastInteractionUtc,
        bool isContact,
        bool isFavorite,
        DateTime nowUtc)
    {
        var usage = Math.Log(1 + 3 * Math.Max(0, sentCount) + Math.Max(0, receivedCount));

        // Old correspondence still counts for something, so a long-time contact never sinks to zero.
        var recency = 0.3;
        if (lastInteractionUtc is { } last)
        {
            var days = Math.Max(0, (nowUtc - last).TotalDays);
            recency += 0.7 * Math.Pow(0.5, days / RecencyHalfLifeDays);
        }

        // Match quality is weighted so a prefix match beats a substring match for all but
        // the people the account writes to constantly.
        return 2 * MatchScore(query, displayName, address)
            + usage * recency
            + (isContact ? 0.5 : 0)
            + (isFavorite ? 1.0 : 0);
    }

    /// <summary>
    /// 3 when a word of the name starts with the query, 2.5 when the address does,
    /// 1.5 for a match inside the name or address, 0 otherwise.
    /// </summary>
    public static double MatchScore(string query, string displayName, string address)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q))
            return 0;

        if (HasWordStartingWith(displayName, q))
            return 3.0;

        if (address?.StartsWith(q, StringComparison.OrdinalIgnoreCase) == true)
            return 2.5;

        if (displayName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true ||
            address?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
            return 1.5;

        return 0;
    }

    private static bool HasWordStartingWith(string text, string query)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        for (var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = text.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (index == 0 || !char.IsLetterOrDigit(text[index - 1]))
                return true;
        }

        return false;
    }
}

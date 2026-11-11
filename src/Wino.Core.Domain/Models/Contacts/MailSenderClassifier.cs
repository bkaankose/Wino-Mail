using System;
using System.Collections.Generic;
using System.Linq;

using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>
/// Decides on the device who sent a mail: a person, an automated service or a newsletter. Only the sender's address and
/// name are read, so it works the same for every provider; a newsletter is an automated sender with marketing markers.
/// </summary>
public static class MailSenderClassifier
{
    private static readonly HashSet<string> NewsletterWords = new(StringComparer.Ordinal)
    {
        "newsletter", "newsletters", "news", "marketing", "mkt", "promo", "promos", "promotion", "promotions",
        "offers", "offer", "deals", "deal", "digest", "weekly", "campaign", "campaigns", "sale", "sales", "shop", "store"
    };

    public static MailSenderKind Classify(string? address, string? displayName)
    {
        var from = address?.Trim() ?? string.Empty;
        var name = displayName?.Trim() ?? string.Empty;
        if (!RecipientAddressHeuristics.IsAutomatedAddress(from, name))
            return MailSenderKind.Person;

        return Words(from).Concat(Words(name)).Any(NewsletterWords.Contains) ? MailSenderKind.Newsletter : MailSenderKind.Service;
    }

    public static bool Matches(MailSenderKind wanted, string? address, string? displayName)
        => wanted == MailSenderKind.Any || Classify(address, displayName) == wanted;

    private static IEnumerable<string> Words(string text)
        => text.ToLowerInvariant().Split(['@', '.', '-', '_', '+', ' ', ',', '|', ':'], StringSplitOptions.RemoveEmptyEntries);
}

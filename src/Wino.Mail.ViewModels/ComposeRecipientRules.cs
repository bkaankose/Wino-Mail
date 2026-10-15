#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The composer's recipient rules shared by the Mac, iPhone/iPad and Android recipient fields (Windows TokenItemAdding,
/// ResolveRecipientSuggestionsAsync and AddListMembers): what typed text counts as an address, when it is complete
/// enough to keep when the field loses focus, which suggestions to offer, and which addresses a picked suggestion adds.
/// </summary>
public static class ComposeRecipientRules
{
    /// <summary>Suggestions start at this many typed characters.</summary>
    public const int MinimumSuggestionQueryLength = 2;

    /// <summary>The pause after typing before suggestions are searched.</summary>
    public static TimeSpan SuggestionDelay { get; } = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Parses typed text ("address" or "Name &lt;address&gt;") into an address with an optional display name. Text
    /// without an <c>@</c> is not an address.
    /// </summary>
    public static bool TryParseTyped(string? text, out string address, out string? name)
    {
        address = string.Empty;
        name = null;
        var typed = text?.Trim();
        if (string.IsNullOrEmpty(typed) || !MailboxAddress.TryParse(typed, out var mailbox) || mailbox.Address?.Contains('@') != true)
            return false;
        address = mailbox.Address;
        name = string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name;
        return true;
    }

    /// <summary>
    /// A complete address (an <c>@</c> and a dot): kept as a recipient when the field loses focus or before saving, and
    /// taken as typed even while suggestions show.
    /// </summary>
    public static bool IsCompleteAddress(string? text)
        => TryParseTyped(text, out var address, out _) && address.Contains('.');

    /// <summary>True when <paramref name="query"/> is long enough to search for suggestions.</summary>
    public static bool ShouldSuggest(string? query) => (query?.Trim().Length ?? 0) >= MinimumSuggestionQueryLength;

    /// <summary>Contacts, remembered correspondents and lists; someone already in the field is not suggested again.</summary>
    public static List<RecipientSuggestion> ExcludeExisting(IEnumerable<RecipientSuggestion>? suggestions, IEnumerable<AccountContact> recipients)
    {
        if (suggestions is null) return [];
        var existing = recipients as ICollection<AccountContact> ?? recipients.ToList();
        return suggestions.Where(suggestion => suggestion is not null
            && (suggestion.IsList || !ComposePageViewModel.ContainsAddress(existing, suggestion.Address))).ToList();
    }

    /// <summary>
    /// The addresses (with display names) a picked suggestion adds to a field: every list member with an address that
    /// is not already there, or the suggestion's own address. Empty when everything is already in the field.
    /// </summary>
    public static List<(string Address, string? Name)> AddressesFor(RecipientSuggestion suggestion, IEnumerable<AccountContact> recipients)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        var existing = recipients as ICollection<AccountContact> ?? recipients.ToList();
        var result = new List<(string Address, string? Name)>();

        void Add(string? address, string? name)
        {
            if (string.IsNullOrWhiteSpace(address) || ComposePageViewModel.ContainsAddress(existing, address)
                || result.Any(item => string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase))) return;
            result.Add((address, string.IsNullOrWhiteSpace(name) ? null : name));
        }

        if (suggestion.IsList)
        {
            foreach (var member in suggestion.ListMembers ?? Array.Empty<AccountContact>())
                if (member is not null) Add(member.PrimaryEmailAddress, member.DisplayName);
        }
        else
        {
            Add(suggestion.Address, suggestion.DisplayName);
        }
        return result;
    }
}

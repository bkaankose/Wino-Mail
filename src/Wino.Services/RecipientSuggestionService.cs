using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Services;

public class RecipientSuggestionService : IRecipientSuggestionService
{
    private const int MinimumQueryLength = 2;
    private const int MaximumListSuggestions = 2;
    private const int MaximumAddressesPerContact = 3;
    private const int CandidateLimit = 50;

    private readonly IContactService _contactService;
    private readonly IRecipientHistoryService _recipientHistoryService;

    public RecipientSuggestionService(IContactService contactService, IRecipientHistoryService recipientHistoryService)
    {
        _contactService = contactService;
        _recipientHistoryService = recipientHistoryService;
    }

    public async Task<List<RecipientSuggestion>> SuggestAsync(Guid? accountId, string query, int limit = 8, bool includeLists = true)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < MinimumQueryLength)
            return [];

        var nowUtc = DateTime.UtcNow;
        var cards = await _contactService.ResolveRecipientCandidatesAsync(accountId, q, CandidateLimit).ConfigureAwait(false) ?? [];
        var history = accountId is Guid id
            ? await _recipientHistoryService.SearchAsync(id, q, CandidateLimit).ConfigureAwait(false)
            : new List<RecipientHistory>();

        var historyByAddress = history
            .Where(row => !string.IsNullOrEmpty(row.NormalizedAddress))
            .GroupBy(row => row.NormalizedAddress, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var bookNames = cards.Count == 0
            ? new Dictionary<Guid, string>()
            : (await _contactService.GetAddressBooksAsync().ConfigureAwait(false))
                .ToDictionary(book => book.Id, book => book.DisplayName);

        var people = new Dictionary<string, RecipientSuggestion>(StringComparer.Ordinal);

        foreach (var card in cards)
        {
            bookNames.TryGetValue(card.AddressBookId, out var bookName);

            foreach (var address in GetSuggestedAddresses(card, q))
            {
                var key = ContactEmailAddress.Normalize(address);
                if (people.ContainsKey(key))
                    continue;

                historyByAddress.TryGetValue(key, out var row);
                var score = RecipientSuggestionRanker.Score(
                    q,
                    card.DisplayValue,
                    address,
                    row?.SentCount ?? 0,
                    row?.ReceivedCount ?? 0,
                    row?.LastInteractionUtc,
                    isContact: true,
                    card.IsFavorite,
                    nowUtc);

                people[key] = RecipientSuggestion.FromContact(card, address, bookName, q, score);
            }
        }

        foreach (var row in history)
        {
            if (people.ContainsKey(row.NormalizedAddress))
                continue;

            var score = RecipientSuggestionRanker.Score(
                q,
                row.DisplayName,
                row.Address,
                row.SentCount,
                row.ReceivedCount,
                row.LastInteractionUtc,
                isContact: false,
                isFavorite: false,
                nowUtc);

            people[row.NormalizedAddress] = RecipientSuggestion.FromHistory(row, Translator.RecipientSuggestion_FromYourMail, q, score);
        }

        var suggestions = new List<RecipientSuggestion>();

        if (includeLists)
        {
            var lists = await _contactService.ResolveRecipientListsAsync(q, MaximumListSuggestions).ConfigureAwait(false) ?? [];
            suggestions.AddRange(lists.Select(list => RecipientSuggestion.FromList(list, q, RecipientSuggestionRanker.MatchScore(q, list.DisplayName, null))));
        }

        suggestions.AddRange(people.Values
            .OrderByDescending(suggestion => suggestion.Score)
            .ThenBy(suggestion => suggestion.DisplayName, StringComparer.OrdinalIgnoreCase));

        return suggestions.Take(Math.Max(1, limit)).ToList();
    }

    /// <summary>
    /// The addresses of a contact worth offering. A contact matched by an address offers that
    /// address, so typing a work address does not silently pick the personal one. A contact
    /// matched by name offers its primary address first.
    /// </summary>
    private static IEnumerable<string> GetSuggestedAddresses(AccountContact card, string query)
    {
        var addresses = (card.EmailAddresses ?? [])
            .Where(email => !string.IsNullOrWhiteSpace(email.Address))
            .OrderByDescending(email => email.IsPrimary)
            .ThenBy(email => email.Order)
            .Select(email => email.Address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count == 0)
            return [];

        var matched = addresses.Where(address => address.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var nameMatches = RecipientSuggestionRanker.MatchScore(query, card.DisplayValue, null) > 0 ||
                          card.CompanyName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

        var selected = nameMatches
            ? addresses.Take(1).Concat(matched)
            : matched.Count > 0 ? matched : addresses.Take(1);

        return selected.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumAddressesPerContact);
    }
}

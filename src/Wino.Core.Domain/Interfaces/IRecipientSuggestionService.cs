using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Suggests recipients while composing, from contacts, contact lists and the account's
/// correspondence history, ranked by match quality and how often they are written to.
/// </summary>
public interface IRecipientSuggestionService
{
    /// <summary>
    /// Suggestions for the typed text. Contact lists come first, then people by score.
    /// Returns an empty list for fewer than two characters.
    /// </summary>
    Task<List<RecipientSuggestion>> SuggestAsync(Guid? accountId, string query, int limit = 8, bool includeLists = true);
}

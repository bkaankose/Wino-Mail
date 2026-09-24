using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>
/// Pure merge of the recipient suggestion sources shared by the mail and calendar compose pages.
///
/// Sources are given in the order they should be offered and each contributes only the addresses the
/// ones before it did not, compared without case - so the earliest source wins a duplicate. The mail
/// composer offers the addresses this mailbox has written to before, then the user's own contacts,
/// then the organisation's directory, because a list ordered by who you actually correspond with
/// beats one ordered by who happens to be in an address book.
/// </summary>
public static class RecipientSuggestionMerge
{
    public static List<AccountContact> Merge(params IReadOnlyList<AccountContact>[] sources)
    {
        var merged = new List<AccountContact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (source == null)
                continue;

            foreach (var contact in source)
            {
                if (contact != null && !string.IsNullOrEmpty(contact.Address) && seen.Add(contact.Address))
                    merged.Add(contact);
            }
        }

        return merged;
    }
}

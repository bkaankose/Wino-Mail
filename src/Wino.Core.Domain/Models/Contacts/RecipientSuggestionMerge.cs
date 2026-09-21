using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>
/// Pure merge of local-contact and Global Address List recipient suggestions shared by the mail and
/// calendar compose pages: local first (the user's own address book), then directory entries whose
/// address is not already present. Local wins on a duplicate address, compared case-insensitively.
/// </summary>
public static class RecipientSuggestionMerge
{
    public static List<AccountContact> Merge(IReadOnlyList<AccountContact> local, IReadOnlyList<AccountContact> directory)
    {
        var merged = new List<AccountContact>(local?.Count ?? 0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (local != null)
        {
            foreach (var contact in local)
            {
                if (contact == null)
                    continue;

                merged.Add(contact);
                if (!string.IsNullOrEmpty(contact.Address))
                    seen.Add(contact.Address);
            }
        }

        if (directory != null)
        {
            foreach (var entry in directory)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.Address) && seen.Add(entry.Address))
                    merged.Add(entry);
            }
        }

        return merged;
    }
}

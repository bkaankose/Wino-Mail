using System;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>
/// Describes which contacts a paged query should return. All members are optional;
/// the default instance matches every contact.
/// </summary>
public record ContactQueryFilter(
    Guid? AddressBookId = null,
    Guid? ListId = null,
    Guid? AccountId = null,
    bool FavoritesOnly = false,
    string SearchQuery = null,
    bool ExcludeRootContacts = false)
{
    public static readonly ContactQueryFilter All = new();
}

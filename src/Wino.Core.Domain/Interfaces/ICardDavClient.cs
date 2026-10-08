using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Interfaces;

public interface ICardDavClient
{
    /// <summary>Resolves the principal and address-book home, then lists the home.</summary>
    Task<CardDavDiscoveryResult> DiscoverAsync(CardDavConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Lists the address books of an already known home, with their current sync token and collection tag.</summary>
    Task<CardDavDiscoveryResult> ListAddressBooksAsync(CardDavConnectionSettings settings, Uri addressBookHomeUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// REPORT sync-collection (RFC 6578). A null token asks for every member; otherwise for
    /// the changes since that token. Returns hrefs and ETags only.
    /// </summary>
    Task<CardDavSyncPage> SyncCollectionAsync(CardDavConnectionSettings settings, CardDavAddressBook addressBook, string syncToken, CancellationToken cancellationToken = default);

    /// <summary>PROPFIND Depth 1 listing of member hrefs and ETags, for servers without sync-collection.</summary>
    Task<IReadOnlyList<CardDavResourceChange>> EnumerateResourcesAsync(CardDavConnectionSettings settings, CardDavAddressBook addressBook, CancellationToken cancellationToken = default);

    /// <summary>
    /// REPORT addressbook-multiget. Results carry the requested href, not the server's
    /// spelling of it. A requested href can be missing from the result.
    /// </summary>
    Task<IReadOnlyList<CardDavResourceChange>> MultiGetAsync(CardDavConnectionSettings settings, CardDavAddressBook addressBook, IReadOnlyList<string> hrefs, CancellationToken cancellationToken = default);
    Task<CardDavResourceChange> GetResourceAsync(CardDavConnectionSettings settings, string exactHref, CancellationToken cancellationToken = default);
    Task<CardDavWriteResult> PutResourceAsync(CardDavConnectionSettings settings, string exactHref, string vcard, string ifMatch = null, bool createOnly = false, CancellationToken cancellationToken = default);
    Task DeleteResourceAsync(CardDavConnectionSettings settings, string exactHref, string ifMatch = null, CancellationToken cancellationToken = default);
    Task<CardDavAddressBook> CreateAddressBookAsync(CardDavConnectionSettings settings, string homeHref, string collectionName, string displayName, CancellationToken cancellationToken = default);
    Task RenameAddressBookAsync(CardDavConnectionSettings settings, string exactHref, string displayName, CancellationToken cancellationToken = default);
    Task DeleteAddressBookAsync(CardDavConnectionSettings settings, string exactHref, CancellationToken cancellationToken = default);
}

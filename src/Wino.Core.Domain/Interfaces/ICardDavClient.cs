using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Interfaces;

public interface ICardDavClient
{
    Task<CardDavDiscoveryResult> DiscoverAsync(CardDavConnectionSettings settings, CancellationToken cancellationToken = default);
    Task<CardDavSyncPage> SyncCollectionAsync(CardDavConnectionSettings settings, CardDavAddressBook addressBook, string syncToken, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CardDavResourceChange>> EnumerateResourcesAsync(CardDavConnectionSettings settings, CardDavAddressBook addressBook, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CardDavResourceChange>> MultiGetAsync(CardDavConnectionSettings settings, CardDavAddressBook addressBook, IReadOnlyList<string> hrefs, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CardDavResourceChange>> QueryAsync(CardDavConnectionSettings settings, CardDavAddressBook addressBook, CancellationToken cancellationToken = default);
    Task<CardDavResourceChange> GetResourceAsync(CardDavConnectionSettings settings, string exactHref, CancellationToken cancellationToken = default);
    Task<CardDavWriteResult> PutResourceAsync(CardDavConnectionSettings settings, string exactHref, string vcard, string ifMatch = null, bool createOnly = false, CancellationToken cancellationToken = default);
    Task DeleteResourceAsync(CardDavConnectionSettings settings, string exactHref, string ifMatch = null, CancellationToken cancellationToken = default);
    Task<CardDavAddressBook> CreateAddressBookAsync(CardDavConnectionSettings settings, string homeHref, string collectionName, string displayName, CancellationToken cancellationToken = default);
    Task RenameAddressBookAsync(CardDavConnectionSettings settings, string exactHref, string displayName, CancellationToken cancellationToken = default);
    Task DeleteAddressBookAsync(CardDavConnectionSettings settings, string exactHref, CancellationToken cancellationToken = default);
}

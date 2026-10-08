using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Persists what CardDAV synchronization knows about the server: discovered address books,
/// per-resource identity and versions, groups and the synchronization checkpoint.
/// </summary>
public interface ICardDavSynchronizationStore
{
    Task<CardDavAccountState> GetAccountStateAsync(Guid accountId);

    /// <summary>
    /// Reconciles local address books with a server listing: adds new ones, updates known
    /// ones and forgets the ones the server no longer has. Returns the forgotten address
    /// books, whose contacts the caller still has to remove.
    /// </summary>
    Task<IReadOnlyList<Guid>> SaveDiscoveryAsync(Guid accountId, CardDavDiscoveryResult discovery);
    Task<IReadOnlyList<CardDavBookBinding>> GetAddressBooksAsync(Guid accountId, Guid? addressBookId = null);

    Task<IReadOnlyList<CardDavResourceShadow>> GetResourcesAsync(Guid addressBookId);
    Task<CardDavResourceShadow> GetResourceByContactAsync(Guid contactId);
    Task<CardDavResourceShadow> GetResourceByListAsync(Guid listId);
    Task SaveResourceAsync(CardDavResourceShadow resource);
    Task DeleteResourceAsync(Guid addressBookId, string exactHref);

    /// <summary>
    /// Records downloaded resources, creates, renames and removes synchronized lists,
    /// rebuilds list membership and, for a final batch, stores the checkpoint.
    /// </summary>
    Task ApplyChangesAsync(CardDavChangeSet changes);

    Task DeleteAddressBookStateAsync(Guid addressBookId);
    Task DeleteAccountStateAsync(Guid accountId);
}

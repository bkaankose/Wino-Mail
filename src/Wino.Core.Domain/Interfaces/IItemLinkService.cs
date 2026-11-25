using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Local links between mails, events and tasks. A link that belongs to a new item is held in memory until the
/// server confirms that item, so a failed create leaves no link. Links never leave the device.
/// </summary>
public interface IItemLinkService
{
    /// <summary>
    /// Holds <paramref name="link"/> until the item created with the client id <paramref name="pendingItemId"/> is
    /// stored after the server confirmed it. The new item is the link's target; its key is set on confirmation.
    /// </summary>
    void ExpectLink(Guid pendingItemId, ItemLink link);

    /// <summary>Stores the links held for <paramref name="pendingItemId"/> under the confirmed item's key.</summary>
    Task ConfirmPendingLinksAsync(Guid pendingItemId, string confirmedKey);

    /// <summary>Drops the links held for an item whose create failed.</summary>
    void DiscardPendingLinks(Guid pendingItemId);

    Task AddLinkAsync(ItemLink link);

    /// <summary>Links where any of <paramref name="keys"/> of an item of <paramref name="kind"/> is the source or the target.</summary>
    Task<List<ItemLink>> GetLinksAsync(ItemLinkKind kind, IReadOnlyCollection<string> keys);

    /// <summary>Removes the links of a deleted item.</summary>
    Task DeleteLinksForItemAsync(ItemLinkKind kind, IReadOnlyCollection<string> keys);

    Task DeleteLinksForAccountAsync(Guid accountId);

    /// <summary>Moves links to an item's new key, for example when a task moves to another provider.</summary>
    Task RekeyAsync(ItemLinkKind kind, string oldKey, string newKey);
}

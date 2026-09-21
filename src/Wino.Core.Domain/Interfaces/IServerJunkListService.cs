using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Pushes an edit of the Safe or Blocked senders list to the account's server copy through its
/// synchronizer, where the provider keeps one (Exchange over MAPI: the junk rule). The local list is
/// the source of truth for the app; this keeps the server's list in step so OWA and Outlook agree.
/// </summary>
public interface IServerJunkListService
{
    /// <summary>Whether the account's provider keeps a server-side copy of the lists this service can edit.</summary>
    Task<bool> SupportsServerJunkListsAsync(MailAccount account);

    /// <summary>
    /// Adds or removes <paramref name="address"/> on the server's list. Returns true when the server
    /// was updated, false when the provider has no server list or the update failed (logged, never thrown).
    /// </summary>
    Task<bool> TryUpdateAsync(Guid accountId, string address, JunkListType listType, bool add, CancellationToken cancellationToken = default);
}

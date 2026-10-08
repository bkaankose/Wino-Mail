using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Writes a contact list of a CardDAV address book to its group on the server. Every
/// method is a no-op for a list that does not belong to a CardDAV address book.
/// </summary>
public interface ICardDavContactListService
{
    Task CreateAsync(ContactList list, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid listId, string name, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid listId, CancellationToken cancellationToken = default);
    Task UpdateMembersAsync(Guid listId, IReadOnlyCollection<Guid> addedContactIds, IReadOnlyCollection<Guid> removedContactIds, CancellationToken cancellationToken = default);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services.CardDav;

public sealed class CardDavSynchronizationStore : BaseDatabaseService, ICardDavSynchronizationStore
{
    private static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromDays(7);

    public CardDavSynchronizationStore(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public Task<CardDavAccountState> GetAccountStateAsync(Guid accountId)
        => Connection.FindAsync<CardDavAccountState>(accountId);

    public async Task<IReadOnlyList<Guid>> SaveDiscoveryAsync(Guid accountId, CardDavDiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var removedBookIds = new List<Guid>();
        await Connection.RunInTransactionAsync(transaction =>
        {
            // Listing a known home leaves the principal untouched and does not renew discovery.
            var account = transaction.Find<CardDavAccountState>(accountId);
            var isFullDiscovery = discovery.ContextUri is not null;
            transaction.InsertOrReplace(new CardDavAccountState
            {
                AccountId = accountId,
                ContextHref = discovery.ContextUri?.AbsoluteUri ?? account?.ContextHref,
                PrincipalHref = discovery.PrincipalUri?.AbsoluteUri ?? account?.PrincipalHref,
                AddressBookHomeHref = discovery.AddressBookHomeUri?.AbsoluteUri ?? account?.AddressBookHomeHref,
                SupportsAddressBookCreation = discovery.SupportsAddressBookCreation,
                DiscoveryExpiresUtc = isFullDiscovery ? DateTime.UtcNow.Add(DiscoveryLifetime) : account?.DiscoveryExpiresUtc,
                RequiresRediscovery = false
            }, typeof(CardDavAccountState));

            var localBooks = transaction.Query<ContactAddressBook>(
                "SELECT * FROM ContactAddressBook WHERE MailAccountId = ? AND SourceKind = ?",
                accountId, (int)ContactSourceKind.CardDav);
            var remoteHrefs = discovery.AddressBooks.Select(book => book.ExactHref).ToHashSet(StringComparer.Ordinal);
            var matchedBookIds = new HashSet<Guid>();

            foreach (var remote in discovery.AddressBooks)
            {
                var book = localBooks.FirstOrDefault(item => string.Equals(item.RemoteId, remote.ExactHref, StringComparison.Ordinal));
                var state = book is null ? null : transaction.Find<CardDavAddressBookState>(book.Id);

                if (book is null)
                {
                    // The same collection on another host (iCloud moves accounts between
                    // shards) keeps its contacts; only the checkpoint is dropped.
                    book = localBooks.FirstOrDefault(item => !matchedBookIds.Contains(item.Id) &&
                        !remoteHrefs.Contains(item.RemoteId) && HasSamePath(item.RemoteId, remote.ExactHref));
                    if (book is not null)
                    {
                        MoveResources(transaction, book.Id, book.RemoteId, remote.ExactHref);
                        state = null;
                    }
                }

                if (book is null)
                {
                    book = new ContactAddressBook
                    {
                        Id = Guid.NewGuid(),
                        MailAccountId = accountId,
                        SourceKind = ContactSourceKind.CardDav,
                        IsDefault = localBooks.Count == 0 && matchedBookIds.Count == 0
                    };
                    transaction.Insert(book, typeof(ContactAddressBook));
                }

                matchedBookIds.Add(book.Id);
                book.RemoteId = remote.ExactHref;
                book.ParentRemoteId = discovery.AddressBookHomeUri?.AbsoluteUri;
                book.DisplayName = !string.IsNullOrWhiteSpace(remote.DisplayName) ? remote.DisplayName
                    : !string.IsNullOrWhiteSpace(book.DisplayName) ? book.DisplayName : "Address book";
                book.IsReadOnly = remote.IsReadOnly;
                transaction.Update(book, typeof(ContactAddressBook));

                // Discovery describes the server. The token and tag describe what was applied
                // locally and only change when a synchronization completes.
                transaction.InsertOrReplace(new CardDavAddressBookState
                {
                    AddressBookId = book.Id,
                    AccountId = accountId,
                    ExactHref = remote.ExactHref,
                    SyncToken = state?.SyncToken,
                    CollectionTag = state?.CollectionTag,
                    LastSyncUtc = state?.LastSyncUtc,
                    SupportsSyncCollection = remote.SupportsSyncCollection,
                    SupportsMultiget = remote.SupportsMultiget,
                    SupportsVCard4 = remote.SupportsVCard4,
                    IsReadOnly = remote.IsReadOnly
                }, typeof(CardDavAddressBookState));
            }

            // An empty listing is far more likely a server hiccup than a user who removed
            // every address book, and acting on it would drop all contacts.
            if (discovery.AddressBooks.Count == 0)
                return;

            foreach (var book in localBooks.Where(item => !matchedBookIds.Contains(item.Id)))
            {
                DeleteBookState(transaction, book.Id);
                removedBookIds.Add(book.Id);
            }
        }).ConfigureAwait(false);

        return removedBookIds;
    }

    public async Task<IReadOnlyList<CardDavBookBinding>> GetAddressBooksAsync(Guid accountId, Guid? addressBookId = null)
    {
        var books = await Connection.Table<ContactAddressBook>()
            .Where(book => book.MailAccountId == accountId && book.SourceKind == ContactSourceKind.CardDav)
            .ToListAsync().ConfigureAwait(false);
        var states = await Connection.Table<CardDavAddressBookState>().Where(state => state.AccountId == accountId).ToListAsync().ConfigureAwait(false);
        var byId = states.ToDictionary(state => state.AddressBookId);

        return books
            .Where(book => (!addressBookId.HasValue || book.Id == addressBookId.Value) && byId.ContainsKey(book.Id))
            .Select(book => new CardDavBookBinding(book, byId[book.Id]))
            .ToList();
    }

    public async Task<IReadOnlyList<CardDavResourceShadow>> GetResourcesAsync(Guid addressBookId)
        => await Connection.Table<CardDavResourceShadow>().Where(resource => resource.AddressBookId == addressBookId).ToListAsync().ConfigureAwait(false);

    public Task<CardDavResourceShadow> GetResourceByContactAsync(Guid contactId)
        => Connection.Table<CardDavResourceShadow>().FirstOrDefaultAsync(resource => resource.ContactId == contactId);

    public Task<CardDavResourceShadow> GetResourceByListAsync(Guid listId)
        => Connection.Table<CardDavResourceShadow>().FirstOrDefaultAsync(resource => resource.ListId == listId);

    public Task SaveResourceAsync(CardDavResourceShadow resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Connection.RunInTransactionAsync(transaction =>
        {
            var current = FindResource(transaction, resource.AddressBookId, resource.ExactHref);
            resource.Id = current?.Id ?? resource.Id;
            transaction.InsertOrReplace(resource, typeof(CardDavResourceShadow));
        });
    }

    public Task DeleteResourceAsync(Guid addressBookId, string exactHref)
        => Connection.ExecuteAsync("DELETE FROM CardDavResourceShadow WHERE AddressBookId = ? AND ExactHref = ?", addressBookId, exactHref);

    public Task ApplyChangesAsync(CardDavChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var href in changes.DeletedHrefs.Distinct(StringComparer.Ordinal))
            {
                var resource = FindResource(transaction, changes.AddressBookId, href);
                if (resource is null) continue;

                DeleteList(transaction, resource.ListId);
                transaction.Delete<CardDavResourceShadow>(resource.Id);
            }

            foreach (var contact in changes.Contacts)
            {
                var resource = FindResource(transaction, changes.AddressBookId, contact.Href)
                               ?? new CardDavResourceShadow { AddressBookId = changes.AddressBookId, ExactHref = contact.Href };

                // The resource was a group until now.
                DeleteList(transaction, resource.ListId);
                resource.Kind = CardDavResourceKind.Contact;
                resource.ContactId = contact.ContactId;
                resource.ListId = null;
                resource.MemberUids = null;
                resource.ETag = contact.ETag;
                resource.Uid = contact.Uid;
                transaction.InsertOrReplace(resource, typeof(CardDavResourceShadow));
            }

            foreach (var group in changes.Groups)
            {
                var resource = FindResource(transaction, changes.AddressBookId, group.Href)
                               ?? new CardDavResourceShadow { AddressBookId = changes.AddressBookId, ExactHref = group.Href };
                var list = resource.ListId is Guid listId ? transaction.Find<ContactList>(listId) : null;
                var name = string.IsNullOrWhiteSpace(group.Name) ? "List" : group.Name.Trim();

                if (list is null)
                {
                    list = new ContactList
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        MailAccountId = changes.AccountId,
                        AddressBookId = changes.AddressBookId,
                        SortOrder = transaction.ExecuteScalar<int>("SELECT COUNT(*) FROM ContactList")
                    };
                    transaction.Insert(list, typeof(ContactList));
                }
                else if (!string.Equals(list.Name, name, StringComparison.Ordinal))
                {
                    list.Name = name;
                    list.ModifiedAtUtc = DateTime.UtcNow;
                    transaction.Update(list, typeof(ContactList));
                }

                resource.Kind = CardDavResourceKind.Group;
                resource.ContactId = null;
                resource.ListId = list.Id;
                resource.MemberUids = string.Join('\n', group.MemberUids);
                resource.ETag = group.ETag;
                resource.Uid = group.Uid;
                transaction.InsertOrReplace(resource, typeof(CardDavResourceShadow));
            }

            RebuildListMembers(transaction, changes.AddressBookId);

            if (!changes.CommitCheckpoint)
                return;

            var now = DateTime.UtcNow;
            transaction.Execute(
                "UPDATE CardDavAddressBookState SET SyncToken = ?, CollectionTag = ?, LastSyncUtc = ? WHERE AddressBookId = ?",
                changes.SyncToken, changes.CollectionTag, now, changes.AddressBookId);
            transaction.Execute("UPDATE ContactAddressBook SET LastSuccessfulSyncUtc = ? WHERE Id = ?", now, changes.AddressBookId);
        });
    }

    public Task DeleteAccountStateAsync(Guid accountId)
        => Connection.RunInTransactionAsync(transaction =>
        {
            var bookIds = transaction.Query<ContactAddressBook>(
                "SELECT * FROM ContactAddressBook WHERE MailAccountId = ? AND SourceKind = ?",
                accountId, (int)ContactSourceKind.CardDav).Select(book => book.Id).ToList();
            foreach (var bookId in bookIds)
                DeleteBookState(transaction, bookId);
            transaction.Delete<CardDavAccountState>(accountId);
        });

    public Task DeleteAddressBookStateAsync(Guid addressBookId)
        => Connection.RunInTransactionAsync(transaction => DeleteBookState(transaction, addressBookId));

    /// <summary>
    /// Group membership is stored by UID, so it is resolved again whenever contacts or
    /// groups of the book change: a member can arrive after the group that names it.
    /// </summary>
    private static void RebuildListMembers(SQLiteConnection transaction, Guid addressBookId)
    {
        var resources = transaction.Query<CardDavResourceShadow>("SELECT * FROM CardDavResourceShadow WHERE AddressBookId = ?", addressBookId);
        var contactsByUid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources.Where(item => item.Kind == CardDavResourceKind.Contact && item.ContactId.HasValue && !string.IsNullOrWhiteSpace(item.Uid)))
            contactsByUid[resource.Uid] = resource.ContactId.Value;

        foreach (var group in resources.Where(item => item.Kind == CardDavResourceKind.Group && item.ListId.HasValue))
        {
            var listId = group.ListId.Value;
            var desired = (group.MemberUids ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(contactsByUid.ContainsKey)
                .Select(uid => contactsByUid[uid])
                .ToHashSet();
            var current = transaction.Query<ContactListMember>("SELECT * FROM ContactListMember WHERE ListId = ?", listId);

            foreach (var member in current.Where(item => !desired.Contains(item.ContactId)))
                transaction.Delete<ContactListMember>(member.Id);
            foreach (var contactId in desired.Except(current.Select(item => item.ContactId)))
                transaction.Insert(new ContactListMember { Id = Guid.NewGuid(), ListId = listId, ContactId = contactId }, typeof(ContactListMember));
        }
    }

    private static CardDavResourceShadow FindResource(SQLiteConnection transaction, Guid addressBookId, string href)
        => transaction.Query<CardDavResourceShadow>(
            "SELECT * FROM CardDavResourceShadow WHERE AddressBookId = ? AND ExactHref = ? LIMIT 1",
            addressBookId, href).FirstOrDefault();

    private static void DeleteList(SQLiteConnection transaction, Guid? listId)
    {
        if (listId is not Guid id) return;

        transaction.Execute("DELETE FROM ContactListMember WHERE ListId = ?", id);
        transaction.Delete<ContactList>(id);
    }

    private static void DeleteBookState(SQLiteConnection transaction, Guid addressBookId)
    {
        transaction.Execute("DELETE FROM ContactListMember WHERE ListId IN (SELECT Id FROM ContactList WHERE AddressBookId = ?)", addressBookId);
        transaction.Execute("DELETE FROM ContactList WHERE AddressBookId = ?", addressBookId);
        transaction.Execute("DELETE FROM CardDavResourceShadow WHERE AddressBookId = ?", addressBookId);
        transaction.Delete<CardDavAddressBookState>(addressBookId);
    }

    private static void MoveResources(SQLiteConnection transaction, Guid addressBookId, string oldHref, string newHref)
    {
        if (string.IsNullOrEmpty(oldHref)) return;

        // Resource hrefs start with their collection href; swap that prefix.
        transaction.Execute(
            "UPDATE CardDavResourceShadow SET ExactHref = ? || substr(ExactHref, ?) WHERE AddressBookId = ? AND substr(ExactHref, 1, ?) = ?",
            newHref, oldHref.Length + 1, addressBookId, oldHref.Length, oldHref);
        transaction.Execute(
            "UPDATE ContactCard SET RemoteId = ? || substr(RemoteId, ?) WHERE AddressBookId = ? AND substr(RemoteId, 1, ?) = ?",
            newHref, oldHref.Length + 1, addressBookId, oldHref.Length, oldHref);
    }

    private static bool HasSamePath(string leftHref, string rightHref)
        => Uri.TryCreate(leftHref, UriKind.Absolute, out var left) &&
           Uri.TryCreate(rightHref, UriKind.Absolute, out var right) &&
           string.Equals(left.AbsolutePath, right.AbsolutePath, StringComparison.Ordinal);
}

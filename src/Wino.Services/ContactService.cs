using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using SQLite;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Services;

public class ContactService : BaseDatabaseService, IContactService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ContactService>();
    private readonly IContactPictureFileService _pictureFileService;

    public ContactService(IDatabaseService databaseService, IContactPictureFileService pictureFileService = null) : base(databaseService)
        => _pictureFileService = pictureFileService;

    public async Task<AccountContact> GetContactAsync(Guid contactId)
    {
        var contact = await Connection.Table<AccountContact>()
            .FirstOrDefaultAsync(item => item.Id == contactId)
            .ConfigureAwait(false);

        if (contact is not null)
            await LoadChildrenAsync([contact]).ConfigureAwait(false);

        return contact;
    }

    public async Task<List<AccountContact>> GetContactsByAddressBookAsync(Guid addressBookId)
    {
        var contacts = await Connection.Table<AccountContact>()
            .Where(contact => contact.AddressBookId == addressBookId)
            .ToListAsync()
            .ConfigureAwait(false);
        await LoadChildrenAsync(contacts).ConfigureAwait(false);
        return contacts;
    }

    public async Task<List<ContactAddressBook>> GetAddressBooksAsync(Guid? accountId = null)
    {
        var books = await Connection.Table<ContactAddressBook>().ToListAsync().ConfigureAwait(false);
        return accountId is null ? books : books.Where(book => book.MailAccountId == accountId.Value).ToList();
    }

    public async Task<ContactAddressBook> GetOrCreateProviderAddressBookAsync(
        Guid accountId,
        ContactSourceKind sourceKind,
        string remoteId,
        string displayName,
        bool isDefault,
        string parentRemoteId = null)
    {
        var books = await Connection.Table<ContactAddressBook>().Where(book => book.MailAccountId == accountId).ToListAsync().ConfigureAwait(false);
        var book = books.FirstOrDefault(item => item.SourceKind == sourceKind && string.Equals(item.RemoteId, remoteId, StringComparison.Ordinal));
        if (book is not null)
        {
            book.DisplayName = displayName;
            book.ParentRemoteId = parentRemoteId;
            book.IsDefault = isDefault;
            await Connection.UpdateAsync(book, typeof(ContactAddressBook)).ConfigureAwait(false);
            return book;
        }

        book = new ContactAddressBook
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            SourceKind = sourceKind,
            RemoteId = remoteId,
            ParentRemoteId = parentRemoteId,
            DisplayName = displayName,
            IsDefault = isDefault
        };
        await Connection.InsertAsync(book, typeof(ContactAddressBook)).ConfigureAwait(false);
        return book;
    }

    public async Task<List<ContactCreateDestination>> GetCreateDestinationsAsync()
    {
        var accounts = await Connection.Table<MailAccount>().ToListAsync().ConfigureAwait(false);
        var books = await Connection.Table<ContactAddressBook>().ToListAsync().ConfigureAwait(false);

        return (from book in books
                join account in accounts on book.MailAccountId equals account.Id
                where account.IsContactAccessEnabled &&
                      (book.SourceKind == ContactSourceKind.Local ||
                       (account.IsContactAccessGranted && !account.IsContactReauthorizationRequired))
                orderby book.IsDefault descending, account.Order, book.DisplayName
                select new ContactCreateDestination(
                    account.Id,
                    book.Id,
                    book.SourceKind,
                    account.Name,
                    book.DisplayName,
                    book.IsDefault))
            .ToList();
    }

    public async Task<List<AccountContact>> ResolveRecipientCandidatesAsync(Guid? accountId, string queryText, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(queryText) || queryText.Trim().Length < 2)
            return null;

        var pattern = $"%{queryText.Trim()}%";
        var contacts = await Connection.QueryAsync<AccountContact>(
            "SELECT DISTINCT c.* FROM ContactCard c LEFT JOIN ContactEmailAddress e ON e.ContactId = c.Id " +
            "WHERE c.PendingMutation <> ? AND (c.DisplayName LIKE ? OR c.CompanyName LIKE ? OR e.Address LIKE ?) " +
            "LIMIT 200",
            (int)ContactPendingMutation.Delete,
            pattern,
            pattern,
            pattern).ConfigureAwait(false);

        await LoadChildrenAsync(contacts).ConfigureAwait(false);

        return contacts
            .Where(contact => !string.IsNullOrWhiteSpace(contact.PrimaryEmailAddress))
            .OrderByDescending(contact => accountId.HasValue && contact.MailAccountId == accountId.Value)
            .ThenByDescending(contact => contact.SourceKind != ContactSourceKind.Local)
            .ThenBy(contact => contact.SourceKind == ContactSourceKind.Local && contact.IsAutoCollected)
            .ThenBy(contact => contact.DisplayValue, StringComparer.OrdinalIgnoreCase)
            .GroupBy(contact => ContactEmailAddress.Normalize(contact.PrimaryEmailAddress), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(Math.Max(1, limit))
            .ToList();
    }

    public async Task<List<ContactListRecipient>> ResolveRecipientListsAsync(string queryText, int limit = 5)
    {
        var trimmed = queryText?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 2)
            return [];

        var lists = await GetContactListsAsync().ConfigureAwait(false);
        var matches = lists
            .Where(list => list.Name?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) == true)
            .Take(Math.Max(1, limit))
            .ToList();

        var results = new List<ContactListRecipient>(matches.Count);
        foreach (var list in matches)
        {
            var page = await GetContactsPageAsync(new ContactQueryFilter(ListId: list.Id), 0, 500).ConfigureAwait(false);
            var members = page.Contacts.Where(contact => !string.IsNullOrWhiteSpace(contact.PrimaryEmailAddress)).ToList();
            if (members.Count == 0)
                continue;

            results.Add(new ContactListRecipient(list, members, string.Format(Translator.ContactList_MemberCount, members.Count)));
        }

        return results;
    }

    public async Task SaveAddressInformationAsync(Guid accountId, MimeMessage message)
    {
        if (message is null)
            return;

        var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(item => item.Id == accountId).ConfigureAwait(false);
        if (account is null || !account.IsContactAccessEnabled || account.IsContactAccessGranted)
            return;

        var book = await GetOrCreateLocalAddressBookAsync(accountId, account.Name).ConfigureAwait(false);
        var contacts = message.GetRecipients(true)
            .Where(address => !string.IsNullOrWhiteSpace(address.Address))
            .Select(address => CreateAutoCollectedContact(accountId, book.Id, address.Address, address.Name));

        await SaveAutoCollectedAsync(accountId, book.Id, contacts).ConfigureAwait(false);
    }

    public async Task SaveAddressInformationAsync(Guid accountId, IEnumerable<AccountContact> contacts)
    {
        if (contacts is null)
            return;

        var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(item => item.Id == accountId).ConfigureAwait(false);
        if (account is null || !account.IsContactAccessEnabled || account.IsContactAccessGranted)
            return;

        var book = await GetOrCreateLocalAddressBookAsync(accountId, account.Name).ConfigureAwait(false);
        var normalized = contacts.Select(contact => CreateAutoCollectedContact(accountId, book.Id, contact.Address, contact.Name));
        await SaveAutoCollectedAsync(accountId, book.Id, normalized).ConfigureAwait(false);
    }

    public async Task<AccountContact> StageCreateAsync(AccountContact contact)
    {
        contact.Id = contact.Id == Guid.Empty ? Guid.NewGuid() : contact.Id;
        contact.CreatedAtUtc = DateTime.UtcNow;
        contact.ModifiedAtUtc = contact.CreatedAtUtc;
        contact.IsAutoCollected = false;
        contact.PendingMutation = contact.SourceKind == ContactSourceKind.Local
            ? ContactPendingMutation.None
            : ContactPendingMutation.Create;

        await WriteContactAsync(contact, insert: true).ConfigureAwait(false);
        return contact;
    }

    public async Task<AccountContact> StageUpdateAsync(AccountContact contact)
    {
        contact.ModifiedAtUtc = DateTime.UtcNow;
        contact.IsAutoCollected = false;
        contact.PendingMutation = contact.SourceKind == ContactSourceKind.Local
            ? ContactPendingMutation.None
            : ContactPendingMutation.Update;

        await WriteContactAsync(contact, insert: false).ConfigureAwait(false);
        return contact;
    }

    public async Task StageDeleteAsync(Guid contactId)
    {
        var contact = await GetContactAsync(contactId).ConfigureAwait(false);
        if (contact is null)
            return;

        if (contact.SourceKind == ContactSourceKind.Local)
            await DeleteCardsAsync([contact]).ConfigureAwait(false);
        else
            await Connection.ExecuteAsync(
                "UPDATE ContactCard SET PendingMutation = ?, ModifiedAtUtc = ? WHERE Id = ?",
                (int)ContactPendingMutation.Delete,
                DateTime.UtcNow,
                contactId).ConfigureAwait(false);
    }

    public Task SetContactPictureFileIdAsync(Guid contactId, Guid? pictureFileId)
        => Connection.ExecuteAsync("UPDATE ContactCard SET ContactPictureFileId = ?, ModifiedAtUtc = ? WHERE Id = ?", pictureFileId, DateTime.UtcNow, contactId);

    public Task SuppressContactPictureAsync(Guid contactId, string remotePhotoKey)
        => Connection.ExecuteAsync(
            "UPDATE ContactCard SET ContactPictureFileId = NULL, RemotePhotoKey = ?, ModifiedAtUtc = ? WHERE Id = ?",
            remotePhotoKey,
            DateTime.UtcNow,
            contactId);

    public async Task CompleteMutationAsync(Guid localContactId, AccountContact serverContact, bool deleted)
    {
        var local = await GetContactAsync(localContactId).ConfigureAwait(false);
        if (local is null)
            return;

        if (deleted)
        {
            await DeleteCardsAsync([local]).ConfigureAwait(false);
            return;
        }

        if (serverContact is null)
        {
            local.PendingMutation = ContactPendingMutation.None;
            await Connection.UpdateAsync(local, typeof(AccountContact)).ConfigureAwait(false);
            return;
        }

        serverContact.Id = local.Id;
        serverContact.MailAccountId = local.MailAccountId;
        serverContact.AddressBookId = local.AddressBookId;
        serverContact.SourceKind = local.SourceKind;
        serverContact.IsFavorite = local.IsFavorite;
        serverContact.PendingMutation = ContactPendingMutation.None;
        if (serverContact.ContactPictureFileId is null)
            serverContact.ContactPictureFileId = local.ContactPictureFileId;
        await WriteContactAsync(serverContact, insert: false).ConfigureAwait(false);
        if (local.ContactPictureFileId.HasValue && local.ContactPictureFileId != serverContact.ContactPictureFileId)
            await DeletePicturesAsync([local]).ConfigureAwait(false);
    }

    public async Task ReplaceAddressBookAsync(Guid addressBookId, IReadOnlyList<AccountContact> contacts, string deltaToken)
    {
        var existing = await Connection.Table<AccountContact>().Where(contact => contact.AddressBookId == addressBookId).ToListAsync().ConfigureAwait(false);
        var normalized = contacts.Select(contact => PrepareRemoteContact(contact, addressBookId)).ToList();
        var existingByRemoteId = existing
            .Where(contact => !string.IsNullOrWhiteSpace(contact.RemoteId))
            .ToDictionary(contact => contact.RemoteId, StringComparer.Ordinal);
        var retainedPictureIds = new HashSet<Guid>();
        var retainedContactIds = new HashSet<Guid>();

        foreach (var contact in normalized)
        {
            if (string.IsNullOrWhiteSpace(contact.RemoteId) || !existingByRemoteId.TryGetValue(contact.RemoteId, out var current))
                continue;

            contact.Id = current.Id;

            // Favorites are local-only and must survive a server-authoritative rebuild.
            contact.IsFavorite = current.IsFavorite;
            retainedContactIds.Add(current.Id);

            if (contact.ContactPictureFileId is null && string.Equals(contact.RemotePhotoKey, current.RemotePhotoKey, StringComparison.Ordinal))
                contact.ContactPictureFileId = current.ContactPictureFileId;

            if (contact.ContactPictureFileId is Guid retainedPictureId)
                retainedPictureIds.Add(retainedPictureId);
        }

        await Connection.RunInTransactionAsync(transaction =>
        {
            // Rows are re-inserted under the same ids, so list membership is only dropped
            // for contacts the server no longer returns.
            DeleteCards(transaction, existing, deleteListMemberships: false);
            foreach (var contact in existing.Where(item => !retainedContactIds.Contains(item.Id)))
                transaction.Execute("DELETE FROM ContactListMember WHERE ContactId = ?", contact.Id);

            foreach (var contact in normalized)
                InsertContact(transaction, contact);

            if (!string.IsNullOrWhiteSpace(deltaToken))
                transaction.Execute("UPDATE ContactAddressBook SET DeltaToken = ?, LastSuccessfulSyncUtc = ? WHERE Id = ?", deltaToken, DateTime.UtcNow, addressBookId);
            else
                transaction.Execute("UPDATE ContactAddressBook SET LastSuccessfulSyncUtc = ? WHERE Id = ?", DateTime.UtcNow, addressBookId);
        }).ConfigureAwait(false);
        await DeletePicturesAsync(existing.Where(contact =>
            contact.ContactPictureFileId is Guid pictureId && !retainedPictureIds.Contains(pictureId))).ConfigureAwait(false);
    }

    public async Task ApplyDeltaAsync(Guid addressBookId, ContactSynchronizationBatch batch, bool commitDeltaToken)
    {
        var replacedPictures = new List<AccountContact>();
        var deletedIds = batch.DeletedRemoteIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? [];
        var deleted = deletedIds.Count == 0
            ? []
            : (await Connection.Table<AccountContact>().Where(contact => contact.AddressBookId == addressBookId).ToListAsync().ConfigureAwait(false))
                .Where(contact => deletedIds.Contains(contact.RemoteId, StringComparer.Ordinal)).ToList();

        await Connection.RunInTransactionAsync(transaction =>
        {
            DeleteCards(transaction, deleted);
            foreach (var contact in batch.Upserts ?? [])
            {
                var prepared = PrepareRemoteContact(contact, addressBookId);
                var current = transaction.Query<AccountContact>(
                    "SELECT * FROM ContactCard WHERE AddressBookId = ? AND RemoteId = ? LIMIT 1",
                    addressBookId,
                    prepared.RemoteId).FirstOrDefault();

                if (current is not null)
                {
                    prepared.Id = current.Id;
                    prepared.IsFavorite = current.IsFavorite;
                    if (prepared.ContactPictureFileId is null && string.Equals(prepared.RemotePhotoKey, current.RemotePhotoKey, StringComparison.Ordinal))
                        prepared.ContactPictureFileId = current.ContactPictureFileId;
                    else if (current.ContactPictureFileId.HasValue && current.ContactPictureFileId != prepared.ContactPictureFileId)
                        replacedPictures.Add(current);
                    DeleteChildRows(transaction, current.Id);
                    transaction.Update(prepared, typeof(AccountContact));
                }
                else
                {
                    InsertContact(transaction, prepared);
                }

                InsertChildren(transaction, prepared);
            }

            if (commitDeltaToken && !string.IsNullOrWhiteSpace(batch.NextDeltaToken))
            {
                transaction.Execute(
                    "UPDATE ContactAddressBook SET DeltaToken = ?, LastSuccessfulSyncUtc = ? WHERE Id = ?",
                    batch.NextDeltaToken,
                    DateTime.UtcNow,
                    addressBookId);
            }
            else if (commitDeltaToken)
            {
                transaction.Execute("UPDATE ContactAddressBook SET LastSuccessfulSyncUtc = ? WHERE Id = ?", DateTime.UtcNow, addressBookId);
            }
        }).ConfigureAwait(false);
        await DeletePicturesAsync(deleted.Concat(replacedPictures)).ConfigureAwait(false);
    }

    public async Task DeleteAccountContactsAsync(Guid accountId)
    {
        var contacts = await Connection.Table<AccountContact>().Where(contact => contact.MailAccountId == accountId).ToListAsync().ConfigureAwait(false);
        await DeleteCardsAsync(contacts).ConfigureAwait(false);
        await Connection.Table<ContactAddressBook>().DeleteAsync(book => book.MailAccountId == accountId).ConfigureAwait(false);
    }

    public async Task DeleteAddressBookAsync(Guid addressBookId)
    {
        var contacts = await Connection.Table<AccountContact>().Where(contact => contact.AddressBookId == addressBookId).ToListAsync().ConfigureAwait(false);
        await DeleteCardsAsync(contacts).ConfigureAwait(false);
        await Connection.DeleteAsync<ContactAddressBook>(addressBookId).ConfigureAwait(false);
    }

    public async Task DeleteAddressBooksBySourceAsync(Guid accountId, ContactSourceKind sourceKind)
    {
        var books = await Connection.Table<ContactAddressBook>()
            .Where(book => book.MailAccountId == accountId && book.SourceKind == sourceKind)
            .ToListAsync().ConfigureAwait(false);
        foreach (var book in books)
            await DeleteAddressBookAsync(book.Id).ConfigureAwait(false);
    }

    public Task<ContactAddressBook> EnsureLocalAddressBookAsync(Guid accountId, string displayName)
        => GetOrCreateLocalAddressBookAsync(accountId, displayName);

    public async Task<AccountContact> GetContactByAddressAsync(Guid? accountId, string address)
    {
        var normalized = ContactEmailAddress.Normalize(address);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var rows = await Connection.QueryAsync<AccountContact>(
            "SELECT c.* FROM ContactCard c JOIN ContactEmailAddress e ON e.ContactId = c.Id " +
            "WHERE e.NormalizedAddress = ? AND c.PendingMutation <> ? LIMIT 20",
            normalized,
            (int)ContactPendingMutation.Delete).ConfigureAwait(false);
        await LoadChildrenAsync(rows).ConfigureAwait(false);
        return rows.OrderByDescending(contact => accountId.HasValue && contact.MailAccountId == accountId.Value)
            .ThenByDescending(contact => contact.SourceKind != ContactSourceKind.Local)
            .ThenBy(contact => contact.IsAutoCollected).FirstOrDefault();
    }

    public async Task<List<AccountContact>> GetContactsByAddressesAsync(Guid? accountId, IEnumerable<string> addresses)
    {
        var results = new List<AccountContact>();
        foreach (var address in addresses?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
        {
            var contact = await GetContactByAddressAsync(accountId, address).ConfigureAwait(false);
            if (contact is not null)
                results.Add(contact);
        }

        return results;
    }

    public async Task<PagedContactsResult> GetContactsPageAsync(ContactQueryFilter filter, int offset, int pageSize)
    {
        filter ??= ContactQueryFilter.All;
        offset = Math.Max(0, offset);
        pageSize = Math.Max(1, pageSize);

        var trimmed = filter.SearchQuery?.Trim();
        var matchAll = string.IsNullOrWhiteSpace(trimmed) ? 1 : 0;
        var pattern = matchAll == 1 ? string.Empty : $"%{trimmed}%";

        var clauses = new List<string>
        {
            "c.PendingMutation <> ?",
            @"(
    ? = 1
    OR c.DisplayName LIKE ?
    OR c.CompanyName LIKE ?
    OR EXISTS (SELECT 1 FROM ContactEmailAddress e WHERE e.ContactId = c.Id AND e.Address LIKE ?)
    OR EXISTS (SELECT 1 FROM ContactPhoneNumber p WHERE p.ContactId = c.Id AND p.Number LIKE ?)
  )"
        };
        var arguments = new List<object> { (int)ContactPendingMutation.Delete, matchAll, pattern, pattern, pattern, pattern };

        if (filter.FavoritesOnly)
            clauses.Add("c.IsFavorite = 1");

        if (filter.AddressBookId is Guid addressBookId)
        {
            clauses.Add("c.AddressBookId = ?");
            arguments.Add(addressBookId);
        }

        if (filter.AccountId is Guid accountId)
        {
            clauses.Add("c.MailAccountId = ?");
            arguments.Add(accountId);
        }

        if (filter.ListId is Guid listId)
        {
            clauses.Add("EXISTS (SELECT 1 FROM ContactListMember m WHERE m.ContactId = c.Id AND m.ListId = ?)");
            arguments.Add(listId);
        }

        var where = $"{Environment.NewLine}FROM ContactCard c{Environment.NewLine}WHERE {string.Join($"{Environment.NewLine}  AND ", clauses)}";
        var filterArguments = arguments.ToArray();
        var totalCount = await Connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) {where}", filterArguments).ConfigureAwait(false);
        var contacts = await Connection.QueryAsync<AccountContact>(
            $"SELECT c.* {where} ORDER BY c.SortKey, c.Id LIMIT ? OFFSET ?",
            [.. filterArguments, pageSize, offset]).ConfigureAwait(false);

        await LoadChildrenAsync(contacts).ConfigureAwait(false);
        return new PagedContactsResult(contacts, totalCount, offset + contacts.Count < totalCount, offset, pageSize);
    }

    public Task SetContactFavoriteAsync(Guid contactId, bool isFavorite)
        => SetContactsFavoriteAsync([contactId], isFavorite);

    public async Task SetContactsFavoriteAsync(IEnumerable<Guid> contactIds, bool isFavorite)
    {
        var ids = contactIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0)
            return;

        const int chunkSize = 400;
        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var chunk = ids.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(",", chunk.Select(_ => "?"));
            await Connection.ExecuteAsync(
                $"UPDATE ContactCard SET IsFavorite = ? WHERE Id IN ({placeholders})",
                [isFavorite ? 1 : 0, .. chunk.Cast<object>()]).ConfigureAwait(false);
        }
    }

    public Task<int> GetFavoriteContactsCountAsync()
        => Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ContactCard WHERE IsFavorite = 1 AND PendingMutation <> ?",
            (int)ContactPendingMutation.Delete);

    public async Task<List<ContactList>> GetContactListsAsync()
    {
        var lists = await Connection.Table<ContactList>().ToListAsync().ConfigureAwait(false);
        return [.. lists.OrderBy(list => list.SortOrder).ThenBy(list => list.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<ContactList> CreateContactListAsync(string name, string description = null)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var existingCount = await Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ContactList").ConfigureAwait(false);
        var list = new ContactList
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            Description = description?.Trim(),
            SortOrder = existingCount,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow
        };

        await Connection.InsertAsync(list, typeof(ContactList)).ConfigureAwait(false);
        return list;
    }

    public Task UpdateContactListAsync(ContactList list)
    {
        if (list is null || list.Id == Guid.Empty)
            return Task.CompletedTask;

        list.Name = list.Name?.Trim();
        list.ModifiedAtUtc = DateTime.UtcNow;
        return Connection.UpdateAsync(list, typeof(ContactList));
    }

    public Task DeleteContactListAsync(Guid listId)
        => Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM ContactListMember WHERE ListId = ?", listId);
            transaction.Delete<ContactList>(listId);
        });

    public async Task AddContactsToListAsync(Guid listId, IEnumerable<Guid> contactIds)
    {
        var ids = contactIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        if (listId == Guid.Empty || ids.Count == 0)
            return;

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var contactId in ids)
            {
                var exists = transaction.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM ContactListMember WHERE ListId = ? AND ContactId = ?", listId, contactId);
                if (exists == 0)
                    transaction.Insert(new ContactListMember { Id = Guid.NewGuid(), ListId = listId, ContactId = contactId }, typeof(ContactListMember));
            }
        }).ConfigureAwait(false);
    }

    public async Task RemoveContactsFromListAsync(Guid listId, IEnumerable<Guid> contactIds)
    {
        var ids = contactIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        if (listId == Guid.Empty || ids.Count == 0)
            return;

        const int chunkSize = 400;
        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var chunk = ids.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(",", chunk.Select(_ => "?"));
            await Connection.ExecuteAsync(
                $"DELETE FROM ContactListMember WHERE ListId = ? AND ContactId IN ({placeholders})",
                [listId, .. chunk.Cast<object>()]).ConfigureAwait(false);
        }
    }

    public async Task<List<Guid>> GetListIdsForContactAsync(Guid contactId)
    {
        var rows = await Connection.QueryAsync<ContactListMember>(
            "SELECT * FROM ContactListMember WHERE ContactId = ?", contactId).ConfigureAwait(false);
        return [.. rows.Select(row => row.ListId).Distinct()];
    }

    public async Task SetListsForContactAsync(Guid contactId, IEnumerable<Guid> listIds)
    {
        if (contactId == Guid.Empty)
            return;

        var desired = listIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        await Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM ContactListMember WHERE ContactId = ?", contactId);
            foreach (var listId in desired)
                transaction.Insert(new ContactListMember { Id = Guid.NewGuid(), ListId = listId, ContactId = contactId }, typeof(ContactListMember));
        }).ConfigureAwait(false);
    }

    public async Task<Dictionary<Guid, int>> GetContactListCountsAsync()
    {
        var rows = await Connection.QueryAsync<ContactListMember>("SELECT * FROM ContactListMember").ConfigureAwait(false);
        return rows.GroupBy(row => row.ListId).ToDictionary(group => group.Key, group => group.Count());
    }

    private async Task<ContactAddressBook> GetOrCreateLocalAddressBookAsync(Guid accountId, string displayName)
    {
        var book = await Connection.Table<ContactAddressBook>()
            .FirstOrDefaultAsync(item => item.MailAccountId == accountId && item.SourceKind == ContactSourceKind.Local)
            .ConfigureAwait(false);
        if (book is not null)
            return book;

        book = new ContactAddressBook
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            SourceKind = ContactSourceKind.Local,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Local contacts" : displayName,
            IsDefault = true
        };
        await Connection.InsertAsync(book, typeof(ContactAddressBook)).ConfigureAwait(false);
        return book;
    }

    private async Task SaveAutoCollectedAsync(Guid accountId, Guid addressBookId, IEnumerable<AccountContact> contacts)
    {
        var input = contacts.Where(contact => contact is not null && !string.IsNullOrWhiteSpace(contact.Address))
            .GroupBy(contact => ContactEmailAddress.Normalize(contact.Address), StringComparer.Ordinal)
            .Select(group => group.First()).ToList();
        if (input.Count == 0)
            return;

        try
        {
            var normalizedAddresses = input
                .Select(item => ContactEmailAddress.Normalize(item.Address))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var existingByAddress = await FindContactsAsync(accountId, addressBookId, normalizedAddresses).ConfigureAwait(false);
            var noisy = new List<AccountContact>();

            foreach (var item in input)
            {
                var normalized = ContactEmailAddress.Normalize(item.Address);
                existingByAddress.TryGetValue(normalized ?? string.Empty, out var existing);

                if (!ShouldPersistAutoCollectedContact(item.Address, item.DisplayName))
                {
                    if (existing is { IsAutoCollected: true })
                        noisy.Add(existing);
                    continue;
                }

                if (existing is null)
                {
                    await WriteContactAsync(item, insert: true).ConfigureAwait(false);
                }
                else if (existing.IsAutoCollected && item.DisplayName != item.Address && existing.DisplayName != item.DisplayName)
                {
                    existing.DisplayName = item.DisplayName;
                    existing.ModifiedAtUtc = DateTime.UtcNow;
                    existing.SortKey = existing.DisplayValue?.ToLowerInvariant() ?? string.Empty;
                    await Connection.UpdateAsync(existing, typeof(AccountContact)).ConfigureAwait(false);
                }
            }

            if (noisy.Count > 0)
                await DeleteCardsAsync(noisy).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save account-scoped auto-collected contacts for {AccountId}.", accountId);
        }
    }

    private async Task<AccountContact> FindContactAsync(Guid accountId, Guid addressBookId, string address)
    {
        var rows = await Connection.QueryAsync<AccountContact>(
            "SELECT c.* FROM ContactCard c JOIN ContactEmailAddress e ON e.ContactId = c.Id " +
            "WHERE c.MailAccountId = ? AND c.AddressBookId = ? AND e.NormalizedAddress = ? LIMIT 1",
            accountId,
            addressBookId,
            ContactEmailAddress.Normalize(address)).ConfigureAwait(false);
        await LoadChildrenAsync(rows).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    private async Task<Dictionary<string, AccountContact>> FindContactsAsync(
        Guid accountId,
        Guid addressBookId,
        IReadOnlyList<string> normalizedAddresses)
    {
        var result = new Dictionary<string, AccountContact>(StringComparer.Ordinal);
        if (normalizedAddresses.Count == 0)
            return result;

        const int chunkSize = 400;
        for (var offset = 0; offset < normalizedAddresses.Count; offset += chunkSize)
        {
            var chunk = normalizedAddresses.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(",", chunk.Select(_ => "?"));
            var rows = await Connection.QueryAsync<AccountContact>(
                "SELECT DISTINCT c.* FROM ContactCard c JOIN ContactEmailAddress e ON e.ContactId = c.Id " +
                $"WHERE c.MailAccountId = ? AND c.AddressBookId = ? AND e.NormalizedAddress IN ({placeholders})",
                [accountId, addressBookId, .. chunk.Cast<object>()]).ConfigureAwait(false);

            await LoadChildrenAsync(rows).ConfigureAwait(false);
            foreach (var contact in rows)
            {
                foreach (var email in contact.EmailAddresses.Where(email => !string.IsNullOrWhiteSpace(email.NormalizedAddress)))
                    result.TryAdd(email.NormalizedAddress, contact);
            }
        }

        return result;
    }

    private static AccountContact CreateAutoCollectedContact(Guid accountId, Guid addressBookId, string address, string displayName)
    {
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            AddressBookId = addressBookId,
            SourceKind = ContactSourceKind.Local,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? address?.Trim() : displayName.Trim(),
            IsAutoCollected = true,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow
        };
        contact.Address = address?.Trim();
        return contact;
    }

    private async Task WriteContactAsync(AccountContact contact, bool insert)
    {
        NormalizeChildren(contact);
        await Connection.RunInTransactionAsync(transaction =>
        {
            if (insert)
                transaction.Insert(contact, typeof(AccountContact));
            else
            {
                DeleteChildRows(transaction, contact.Id);
                transaction.Update(contact, typeof(AccountContact));
            }

            InsertChildren(transaction, contact);
        }).ConfigureAwait(false);
    }

    private async Task LoadChildrenAsync(IReadOnlyCollection<AccountContact> contacts)
    {
        if (contacts.Count == 0)
            return;

        var contactIds = contacts.Select(contact => contact.Id).Distinct().ToList();
        var emails = await LoadChildRowsAsync<ContactEmailAddress>("ContactEmailAddress", contactIds, item => item.ContactId).ConfigureAwait(false);
        var phones = await LoadChildRowsAsync<ContactPhoneNumber>("ContactPhoneNumber", contactIds, item => item.ContactId).ConfigureAwait(false);
        var addresses = await LoadChildRowsAsync<ContactPostalAddress>("ContactPostalAddress", contactIds, item => item.ContactId).ConfigureAwait(false);
        var ims = await LoadChildRowsAsync<ContactImAddress>("ContactImAddress", contactIds, item => item.ContactId).ConfigureAwait(false);
        var relations = await LoadChildRowsAsync<ContactRelation>("ContactRelation", contactIds, item => item.ContactId).ConfigureAwait(false);

        foreach (var contact in contacts)
        {
            contact.EmailAddresses = emails[contact.Id].OrderBy(item => item.Order).ToList();
            contact.PhoneNumbers = phones[contact.Id].OrderBy(item => item.Order).ToList();
            contact.PostalAddresses = addresses[contact.Id].ToList();
            contact.ImAddresses = ims[contact.Id].OrderBy(item => item.Order).ToList();
            contact.Relations = relations[contact.Id].OrderBy(item => item.Order).ToList();
        }
    }

    private async Task<ILookup<Guid, T>> LoadChildRowsAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        string tableName,
        IReadOnlyList<Guid> contactIds,
        Func<T, Guid> contactIdSelector) where T : new()
    {
        const int chunkSize = 400;
        var rows = new List<T>(contactIds.Count);

        for (var offset = 0; offset < contactIds.Count; offset += chunkSize)
        {
            var chunk = contactIds.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(",", chunk.Select(_ => "?"));
            rows.AddRange(await Connection.QueryAsync<T>(
                $"SELECT * FROM {tableName} WHERE ContactId IN ({placeholders})",
                chunk.Cast<object>().ToArray()).ConfigureAwait(false));
        }

        return rows.ToLookup(contactIdSelector);
    }

    private async Task DeleteCardsAsync(IReadOnlyCollection<AccountContact> contacts)
    {
        await Connection.RunInTransactionAsync(transaction => DeleteCards(transaction, contacts)).ConfigureAwait(false);
        await DeletePicturesAsync(contacts).ConfigureAwait(false);
    }

    private async Task DeletePicturesAsync(IEnumerable<AccountContact> contacts)
    {
        if (_pictureFileService is null)
            return;

        foreach (var pictureId in contacts.Where(contact => contact.ContactPictureFileId.HasValue)
                     .Select(contact => contact.ContactPictureFileId.Value).Distinct())
        {
            try { await _pictureFileService.DeleteContactPictureAsync(pictureId).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warning(ex, "Failed to delete contact picture {PictureId}.", pictureId); }
        }
    }

    private static void DeleteCards(SQLiteConnection transaction, IEnumerable<AccountContact> contacts, bool deleteListMemberships = true)
    {
        foreach (var contact in contacts)
        {
            DeleteChildRows(transaction, contact.Id);
            if (deleteListMemberships)
                transaction.Execute("DELETE FROM ContactListMember WHERE ContactId = ?", contact.Id);
            transaction.Delete<AccountContact>(contact.Id);
        }
    }

    private static void DeleteChildRows(SQLiteConnection transaction, Guid contactId)
    {
        transaction.Execute("DELETE FROM ContactEmailAddress WHERE ContactId = ?", contactId);
        transaction.Execute("DELETE FROM ContactPhoneNumber WHERE ContactId = ?", contactId);
        transaction.Execute("DELETE FROM ContactPostalAddress WHERE ContactId = ?", contactId);
        transaction.Execute("DELETE FROM ContactImAddress WHERE ContactId = ?", contactId);
        transaction.Execute("DELETE FROM ContactRelation WHERE ContactId = ?", contactId);
    }

    private static void InsertContact(SQLiteConnection transaction, AccountContact contact)
    {
        NormalizeChildren(contact);
        transaction.Insert(contact, typeof(AccountContact));
        InsertChildren(transaction, contact);
    }

    private static void InsertChildren(SQLiteConnection transaction, AccountContact contact)
    {
        NormalizeChildren(contact);
        transaction.InsertAll(contact.EmailAddresses, typeof(ContactEmailAddress));
        transaction.InsertAll(contact.PhoneNumbers, typeof(ContactPhoneNumber));
        transaction.InsertAll(contact.PostalAddresses, typeof(ContactPostalAddress));
        transaction.InsertAll(contact.ImAddresses, typeof(ContactImAddress));
        transaction.InsertAll(contact.Relations, typeof(ContactRelation));
    }

    private static void NormalizeChildren(AccountContact contact)
    {
        contact.EmailAddresses ??= [];
        contact.PhoneNumbers ??= [];
        contact.PostalAddresses ??= [];
        contact.ImAddresses ??= [];
        contact.Relations ??= [];

        contact.EmailAddresses = contact.EmailAddresses
            .Where(item => !string.IsNullOrWhiteSpace(item.Address))
            .Take(3)
            .GroupBy(item => ContactEmailAddress.Normalize(item.Address), StringComparer.Ordinal)
            .Select(group => group.First()).ToList();

        for (var index = 0; index < contact.EmailAddresses.Count; index++)
        {
            var item = contact.EmailAddresses[index];
            item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
            item.ContactId = contact.Id;
            item.Address = item.Address.Trim();
            item.NormalizedAddress = ContactEmailAddress.Normalize(item.Address);
            item.Order = index;
            item.IsPrimary = index == 0 || item.IsPrimary;
        }

        foreach (var item in contact.PhoneNumbers) { item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; item.ContactId = contact.Id; }
        foreach (var item in contact.PostalAddresses) { item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; item.ContactId = contact.Id; }
        foreach (var item in contact.ImAddresses) { item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; item.ContactId = contact.Id; }
        foreach (var item in contact.Relations) { item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; item.ContactId = contact.Id; }
        contact.SortKey = contact.DisplayValue?.ToLowerInvariant() ?? string.Empty;
    }

    private static AccountContact PrepareRemoteContact(AccountContact contact, Guid addressBookId)
    {
        contact.Id = contact.Id == Guid.Empty ? Guid.NewGuid() : contact.Id;
        contact.AddressBookId = addressBookId;
        contact.PendingMutation = ContactPendingMutation.None;
        contact.IsAutoCollected = false;
        contact.ModifiedAtUtc = DateTime.UtcNow;
        if (contact.CreatedAtUtc == default)
            contact.CreatedAtUtc = contact.ModifiedAtUtc;
        NormalizeChildren(contact);
        return contact;
    }

    private static bool ShouldPersistAutoCollectedContact(string address, string displayName)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;
        var atIndex = address.Trim().LastIndexOf('@');
        if (atIndex <= 0 || atIndex == address.Trim().Length - 1)
            return false;

        var local = address.Trim()[..atIndex].ToLowerInvariant();
        if (local.StartsWith("reply+", StringComparison.Ordinal) || local.Contains("noreply", StringComparison.Ordinal) ||
            local.Contains("no-reply", StringComparison.Ordinal) || local.Contains("donotreply", StringComparison.Ordinal) ||
            local.Contains("do-not-reply", StringComparison.Ordinal) || local is "mailer-daemon" or "postmaster")
            return false;

        if (local is "notification" or "notifications" or "updates" or "digest")
        {
            var value = displayName?.Trim().ToLowerInvariant() ?? string.Empty;
            return !(value.Contains("notification", StringComparison.Ordinal) || value.Contains("issue #", StringComparison.Ordinal) ||
                     value.Contains("pull request #", StringComparison.Ordinal) || value.Contains("discussion #", StringComparison.Ordinal) ||
                     (value.StartsWith("[", StringComparison.Ordinal) && value.Contains('/') && value.Contains(']')));
        }

        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Core.Domain.Interfaces;

public interface IContactService : IContactQueryService
{
    Task<List<AccountContact>> GetContactsByAddressBookAsync(Guid addressBookId);
    Task<ContactAddressBook> GetOrCreateProviderAddressBookAsync(Guid accountId, ContactSourceKind sourceKind, string remoteId, string displayName, bool isDefault, string parentRemoteId = null);
    Task<List<AccountContact>> ResolveRecipientCandidatesAsync(Guid? accountId, string queryText, int limit = 20);

    /// <summary>
    /// Local contact lists whose name matches the query, each carrying its members so the
    /// caller can expand the list into individual recipients.
    /// </summary>
    Task<List<ContactListRecipient>> ResolveRecipientListsAsync(string queryText, int limit = 5);
    Task SaveAddressInformationAsync(Guid accountId, MimeMessage message);
    Task SaveAddressInformationAsync(Guid accountId, IEnumerable<AccountContact> contacts);
    Task<AccountContact> StageCreateAsync(AccountContact contact);
    Task<AccountContact> StageUpdateAsync(AccountContact contact);
    Task SetContactPictureFileIdAsync(Guid contactId, Guid? pictureFileId);
    Task SuppressContactPictureAsync(Guid contactId, string remotePhotoKey);
    Task StageDeleteAsync(Guid contactId);
    Task CompleteMutationAsync(Guid localContactId, AccountContact serverContact, bool deleted);
    Task ReplaceAddressBookAsync(Guid addressBookId, IReadOnlyList<AccountContact> contacts, string deltaToken);
    Task ApplyDeltaAsync(Guid addressBookId, ContactSynchronizationBatch batch, bool commitDeltaToken);
    Task DeleteAccountContactsAsync(Guid accountId);
    Task DeleteAddressBookAsync(Guid addressBookId);
    Task DeleteAddressBooksBySourceAsync(Guid accountId, ContactSourceKind sourceKind);
    Task<ContactAddressBook> EnsureLocalAddressBookAsync(Guid accountId, string displayName);

    Task<AccountContact> GetContactByAddressAsync(Guid? accountId, string address);
    Task<List<AccountContact>> GetContactsByAddressesAsync(Guid? accountId, IEnumerable<string> addresses);

    // Favorites. Local-only; never pushed to a provider.
    Task SetContactFavoriteAsync(Guid contactId, bool isFavorite);
    Task SetContactsFavoriteAsync(IEnumerable<Guid> contactIds, bool isFavorite);

    // Local contact lists.
    Task<ContactList> CreateContactListAsync(string name, string description = null);
    Task SaveContactListAsync(ContactList list);
    Task UpdateContactListAsync(ContactList list);
    Task DeleteContactListAsync(Guid listId);
    Task AddContactsToListAsync(Guid listId, IEnumerable<Guid> contactIds);
    Task RemoveContactsFromListAsync(Guid listId, IEnumerable<Guid> contactIds);
    Task SetListsForContactAsync(Guid contactId, IEnumerable<Guid> listIds);
}

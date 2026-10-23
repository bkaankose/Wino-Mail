using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Contacts;

namespace MailCal.Core.Domain.Interfaces;

public interface IContactQueryService
{
    Task<AccountContact> GetContactAsync(Guid contactId);
    Task<List<ContactAddressBook>> GetAddressBooksAsync(Guid? accountId = null);
    Task<List<ContactCreateDestination>> GetCreateDestinationsAsync();
    Task<PagedContactsResult> GetContactsPageAsync(ContactQueryFilter filter, int offset, int pageSize, ContactSortOrder sortOrder = ContactSortOrder.ProviderDisplayName);
    Task<int> GetFavoriteContactsCountAsync();
    Task<List<ContactList>> GetContactListsAsync();
    Task<ContactList> GetContactListAsync(Guid listId);
    Task<List<Guid>> GetListIdsForContactAsync(Guid contactId);
    Task<List<AccountContact>> GetContactsByCategoryAsync(Guid categoryId);
    Task<Dictionary<Guid, int>> GetContactListCountsAsync();
}

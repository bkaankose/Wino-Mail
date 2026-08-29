using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Core.Domain.Interfaces;

public interface IContactQueryService
{
    Task<AccountContact> GetContactAsync(Guid contactId);
    Task<List<ContactAddressBook>> GetAddressBooksAsync(Guid? accountId = null);
    Task<List<ContactCreateDestination>> GetCreateDestinationsAsync();
    Task<PagedContactsResult> GetContactsPageAsync(ContactQueryFilter filter, int offset, int pageSize, ContactSortOrder sortOrder = ContactSortOrder.ProviderDisplayName);
    Task<int> GetFavoriteContactsCountAsync();
    Task<List<ContactList>> GetContactListsAsync();
    Task<List<Guid>> GetListIdsForContactAsync(Guid contactId);
    Task<Dictionary<Guid, int>> GetContactListCountsAsync();
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Core.Domain.Interfaces;

public interface IContactService
{
    Task<List<AccountContact>> GetAddressInformationAsync(string queryText);
    Task<AccountContact> GetAddressInformationByAddressAsync(string address);
    Task<List<AccountContact>> GetContactsByAddressesAsync(IEnumerable<string> addresses);
    Task SaveAddressInformationAsync(MimeMessage message);
    Task SaveAddressInformationAsync(IEnumerable<AccountContact> contacts);
    Task<AccountContact> CreateNewContactAsync(string address, string displayName);
    
    // Paged contact queries for ContactsPage
    Task<List<AccountContact>> GetAllContactsAsync();
    Task<List<AccountContact>> SearchContactsAsync(string searchQuery);
    Task<PagedContactsResult> GetContactsPageAsync(int offset, int pageSize, string searchQuery = null, bool excludeRootContacts = false);
    Task<AccountContact> UpdateContactAsync(AccountContact contact);
    Task DeleteContactAsync(string address);
    Task DeleteContactsAsync(IEnumerable<string> addresses);

    // Group / distribution list support
    Task<List<ContactGroup>> GetGroupsAsync();
    Task<ContactGroup> CreateGroupAsync(string name, string description = null);
    Task DeleteGroupAsync(Guid groupId);
    Task<List<AccountContact>> GetGroupMembersAsync(Guid groupId);
    Task AddGroupMemberAsync(Guid groupId, string memberAddress);
    Task RemoveGroupMemberAsync(Guid groupId, string memberAddress);

    /// <summary>
    /// Expands a contact group to the individual <see cref="AccountContact"/> entries of its members.
    /// Returns an empty list if the group does not exist or has no members.
    /// </summary>
    Task<List<AccountContact>> ExpandGroupAsync(Guid groupId);
}

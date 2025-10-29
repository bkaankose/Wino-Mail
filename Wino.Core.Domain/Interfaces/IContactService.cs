using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IContactService
{
    Task<List<AccountContact>> GetAddressInformationAsync(string queryText);
    Task<AccountContact> GetAddressInformationByAddressAsync(string address);
    Task SaveAddressInformationAsync(MimeMessage message);
    Task<AccountContact> CreateNewContactAsync(string address, string displayName);
    
    // New methods for ContactsPage
    Task<List<AccountContact>> GetAllContactsAsync();
    Task<List<AccountContact>> SearchContactsAsync(string searchQuery);
    Task<AccountContact> UpdateContactAsync(AccountContact contact);
    Task DeleteContactAsync(string address);
    Task DeleteContactsAsync(IEnumerable<string> addresses);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Extensions;

namespace Wino.Services;

public class ContactService : BaseDatabaseService, IContactService
{
    public ContactService(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<AccountContact> CreateNewContactAsync(string address, string displayName)
    {
        var contact = new AccountContact() { Address = address, Name = displayName };

        await Connection.InsertAsync(contact, typeof(AccountContact)).ConfigureAwait(false);

        return contact;
    }

    public Task<List<AccountContact>> GetAddressInformationAsync(string queryText)
    {
        if (queryText == null || queryText.Length < 2)
            return Task.FromResult<List<AccountContact>>(null);

        const string query = "SELECT * FROM AccountContact WHERE Address LIKE ? OR Name LIKE ?";
        var pattern = $"%{queryText}%";
        return Connection.QueryAsync<AccountContact>(query, pattern, pattern);
    }

    public Task<AccountContact> GetAddressInformationByAddressAsync(string address)
        => Connection.Table<AccountContact>().FirstOrDefaultAsync(a => a.Address == address);

    public async Task SaveAddressInformationAsync(MimeMessage message)
    {
        var recipients = message
                    .GetRecipients(true)
                    .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Address));

        var addressInformations = recipients.Select(a => new AccountContact() { Name = a.Name, Address = a.Address });

        foreach (var info in addressInformations)
        {
            var currentContact = await GetAddressInformationByAddressAsync(info.Address).ConfigureAwait(false);

            try
            {
                if (currentContact == null)
                {
                    await Connection.InsertAsync(info, typeof(AccountContact)).ConfigureAwait(false);
                }
                else if (!currentContact.IsRootContact && !currentContact.IsOverridden) // Don't update root contacts or overridden contacts.
                {
                    await Connection.InsertOrReplaceAsync(info, typeof(AccountContact)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to add contact information to the database.", ex);
            }
        }
    }

    public Task<List<AccountContact>> GetAllContactsAsync()
    {
        return Connection.Table<AccountContact>().ToListAsync();
    }

    public Task<List<AccountContact>> SearchContactsAsync(string searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return GetAllContactsAsync();

        const string query = "SELECT * FROM AccountContact WHERE Address LIKE ? OR Name LIKE ?";
        var pattern = $"%{searchQuery.Trim()}%";
        return Connection.QueryAsync<AccountContact>(query, pattern, pattern);
    }

    public async Task<AccountContact> UpdateContactAsync(AccountContact contact)
    {
        // Mark the contact as overridden when manually updated
        contact.IsOverridden = true;
        
        await Connection.UpdateAsync(contact, typeof(AccountContact)).ConfigureAwait(false);
        
        return contact;
    }

    public async Task DeleteContactAsync(string address)
    {
        var contact = await GetAddressInformationByAddressAsync(address).ConfigureAwait(false);
        
        if (contact != null && !contact.IsRootContact)
        {
            await Connection.DeleteAsync<AccountContact>(contact).ConfigureAwait(false);
        }
    }

    public async Task DeleteContactsAsync(IEnumerable<string> addresses)
    {
        foreach (var address in addresses)
        {
            await DeleteContactAsync(address).ConfigureAwait(false);
        }
    }
}

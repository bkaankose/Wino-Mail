using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class ContactService : BaseDatabaseService, IContactService
{
    public ContactService(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<AccountContact> CreateNewContactAsync(string address, string displayName)
    {
        var contact = new AccountContact() { Address = address, Name = displayName };

        using var context = ContextFactory.CreateDbContext();
        context.AccountContacts.Add(contact);
        await context.SaveChangesAsync().ConfigureAwait(false);

        return contact;
    }

    public async Task<List<AccountContact>> GetAddressInformationAsync(string queryText)
    {
        if (queryText == null || queryText.Length < 2)
            return null;

        using var context = ContextFactory.CreateDbContext();
        // EF Core LINQ equivalent of SqlKata WhereContains
        return await context.AccountContacts
            .Where(a => EF.Functions.Like(a.Address, $"%{queryText}%") || 
                       EF.Functions.Like(a.Name, $"%{queryText}%"))
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<AccountContact> GetAddressInformationByAddressAsync(string address)
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.AccountContacts
            .FirstOrDefaultAsync(a => a.Address == address)
            .ConfigureAwait(false);
    }

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
                using var context = ContextFactory.CreateDbContext();
                if (currentContact == null)
                {
                    context.AccountContacts.Add(info);
                    await context.SaveChangesAsync().ConfigureAwait(false);
                }
                else if (!currentContact.IsRootContact && !currentContact.IsOverridden) // Don't update root contacts or overridden contacts.
                {
                    // Update existing contact
                    currentContact.Name = info.Name;
                    context.AccountContacts.Update(currentContact);
                    await context.SaveChangesAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to add contact information to the database.", ex);
            }
        }
    }

    public async Task<List<AccountContact>> GetAllContactsAsync()
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.AccountContacts.ToListAsync().ConfigureAwait(false);
    }

    public async Task<List<AccountContact>> SearchContactsAsync(string searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return await GetAllContactsAsync().ConfigureAwait(false);

        var trimmedQuery = searchQuery.Trim();

        using var context = ContextFactory.CreateDbContext();
        // EF Core LINQ equivalent of SqlKata WhereContains
        return await context.AccountContacts
            .Where(a => EF.Functions.Like(a.Address, $"%{trimmedQuery}%") || 
                       EF.Functions.Like(a.Name, $"%{trimmedQuery}%"))
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<AccountContact> UpdateContactAsync(AccountContact contact)
    {
        // Mark the contact as overridden when manually updated
        contact.IsOverridden = true;
        
        using var context = ContextFactory.CreateDbContext();
        context.AccountContacts.Update(contact);
        await context.SaveChangesAsync().ConfigureAwait(false);
        
        return contact;
    }

    public async Task DeleteContactAsync(string address)
    {
        var contact = await GetAddressInformationByAddressAsync(address).ConfigureAwait(false);
        
        if (contact != null && !contact.IsRootContact)
        {
            using var context = ContextFactory.CreateDbContext();
            context.AccountContacts.Remove(contact);
            await context.SaveChangesAsync().ConfigureAwait(false);
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

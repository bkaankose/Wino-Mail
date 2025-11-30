using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Extensions;

namespace Wino.Services;

public class ContactService : BaseDatabaseService, IContactService
{
    public ContactService(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<Contact> CreateContactAsync(string emailAddress, string displayName, Guid? accountId = null)
    {
        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName ?? emailAddress,
            Source = ContactSource.EmailExtraction,
            AccountId = accountId,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow,
            SyncStatus = ContactSyncStatus.Synced
        };

        await Connection.InsertAsync(contact, typeof(Contact)).ConfigureAwait(false);

        // Create the email entry
        var contactEmail = new ContactEmail
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            Address = emailAddress,
            DisplayName = displayName,
            IsPrimary = true,
            Order = 0
        };

        await Connection.InsertAsync(contactEmail, typeof(ContactEmail)).ConfigureAwait(false);

        return contact;
    }

    public async Task<Contact> CreateContactAsync(Contact contact)
    {
        if (contact.Id == Guid.Empty)
            contact.Id = Guid.NewGuid();

        contact.CreatedDate = DateTime.UtcNow;
        contact.ModifiedDate = DateTime.UtcNow;
        contact.Source = ContactSource.Manual;
        contact.SyncStatus = ContactSyncStatus.Synced;

        await Connection.InsertAsync(contact, typeof(Contact)).ConfigureAwait(false);

        return contact;
    }

    public async Task<List<Contact>> SearchContactsAsync(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText) || queryText.Length < 2)
            return new List<Contact>();

        // Search in both Contact and ContactEmail tables
        var pattern = $"%{queryText.Trim()}%";
        
        // Get contact IDs from email search
        var emailMatches = await Connection.QueryAsync<ContactEmail>(
            "SELECT * FROM ContactEmail WHERE Address LIKE ?",
            pattern).ConfigureAwait(false);

        var contactIds = emailMatches.Select(e => e.ContactId).Distinct().ToList();

        // Get contacts by display name or from email matches
        var contacts = await Connection.QueryAsync<Contact>(
            "SELECT * FROM Contact WHERE DisplayName LIKE ? OR Id IN (SELECT ContactId FROM ContactEmail WHERE Address LIKE ?)",
            pattern, pattern).ConfigureAwait(false);

        return contacts;
    }

    public async Task<Contact> GetContactByEmailAsync(string emailAddress)
    {
        if (string.IsNullOrEmpty(emailAddress))
            return null;

        var contactEmail = await Connection.Table<ContactEmail>()
            .FirstOrDefaultAsync(e => e.Address == emailAddress)
            .ConfigureAwait(false);

        if (contactEmail == null)
            return null;

        return await Connection.Table<Contact>()
            .FirstOrDefaultAsync(c => c.Id == contactEmail.ContactId)
            .ConfigureAwait(false);
    }

    public async Task SaveAddressInformationAsync(MimeMessage message, Guid? accountId = null)
    {
        var recipients = message
            .GetRecipients(true)
            .Where(a => !string.IsNullOrEmpty(a.Address));

        foreach (var recipient in recipients)
        {
            try
            {
                await GetOrCreateContactFromEmailAsync(recipient.Address, recipient.Name, accountId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save contact information for {Address}", recipient.Address);
            }
        }
    }

    public async Task<Contact> GetOrCreateContactFromEmailAsync(string emailAddress, string displayName, Guid? accountId = null)
    {
        if (string.IsNullOrEmpty(emailAddress))
            return null;

        // Try to find existing contact by email
        var existingContact = await GetContactByEmailAsync(emailAddress).ConfigureAwait(false);
        
        if (existingContact != null)
        {
            // Update display name if it's better than what we have and contact wasn't manually modified
            if (!existingContact.HasLocalModifications && 
                !string.IsNullOrWhiteSpace(displayName) && 
                existingContact.DisplayName != displayName)
            {
                existingContact.DisplayName = displayName;
                existingContact.ModifiedDate = DateTime.UtcNow;
                await Connection.UpdateAsync(existingContact, typeof(Contact)).ConfigureAwait(false);
            }
            
            return existingContact;
        }

        // Create new contact
        return await CreateContactAsync(emailAddress, displayName, accountId).ConfigureAwait(false);
    }

    public async Task<List<Contact>> GetAllContactsAsync(Guid? accountId = null)
    {
        if (accountId.HasValue)
        {
            return await Connection.Table<Contact>()
                .Where(c => c.AccountId == accountId.Value)
                .ToListAsync()
                .ConfigureAwait(false);
        }

        return await Connection.Table<Contact>()
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<Contact> UpdateContactAsync(Contact contact)
    {
        // Mark the contact as having local modifications when manually updated
        contact.HasLocalModifications = true;
        contact.ModifiedDate = DateTime.UtcNow;
        
        await Connection.UpdateAsync(contact, typeof(Contact)).ConfigureAwait(false);
        
        return contact;
    }

    public async Task DeleteContactAsync(Guid contactId)
    {
        var contact = await Connection.Table<Contact>()
            .FirstOrDefaultAsync(c => c.Id == contactId)
            .ConfigureAwait(false);
        
        if (contact != null && !contact.IsRootContact)
        {
            // Delete related entities first
            await Connection.ExecuteAsync("DELETE FROM ContactEmail WHERE ContactId = ?", contactId).ConfigureAwait(false);
            await Connection.ExecuteAsync("DELETE FROM ContactPhone WHERE ContactId = ?", contactId).ConfigureAwait(false);
            await Connection.ExecuteAsync("DELETE FROM ContactAddress WHERE ContactId = ?", contactId).ConfigureAwait(false);
            
            // Delete the contact
            await Connection.DeleteAsync<Contact>(contact.Id).ConfigureAwait(false);
        }
    }

    public async Task DeleteContactsAsync(IEnumerable<Guid> contactIds)
    {
        foreach (var contactId in contactIds)
        {
            await DeleteContactAsync(contactId).ConfigureAwait(false);
        }
    }
}

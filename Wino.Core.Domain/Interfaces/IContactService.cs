using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IContactService
{
    /// <summary>
    /// Search for contacts by query text (searches name and email addresses).
    /// </summary>
    Task<List<Contact>> SearchContactsAsync(string queryText);
    
    /// <summary>
    /// Get a contact by email address.
    /// </summary>
    Task<Contact> GetContactByEmailAsync(string emailAddress);
    
    /// <summary>
    /// Save contact information extracted from email message headers (From, To, Cc, Bcc).
    /// Creates new contacts or updates existing ones based on email addresses.
    /// </summary>
    Task SaveAddressInformationAsync(MimeMessage message, Guid? accountId = null);
    
    /// <summary>
    /// Create a new contact with a single email address.
    /// </summary>
    Task<Contact> CreateContactAsync(string emailAddress, string displayName, Guid? accountId = null);
    
    /// <summary>
    /// Get all contacts, optionally filtered by account.
    /// </summary>
    Task<List<Contact>> GetAllContactsAsync(Guid? accountId = null);
    
    /// <summary>
    /// Update an existing contact.
    /// </summary>
    Task<Contact> UpdateContactAsync(Contact contact);
    
    /// <summary>
    /// Delete a contact by ID.
    /// </summary>
    Task DeleteContactAsync(Guid contactId);
    
    /// <summary>
    /// Delete multiple contacts by their IDs.
    /// </summary>
    Task DeleteContactsAsync(IEnumerable<Guid> contactIds);
    
    /// <summary>
    /// Get or create a contact from an email address and display name.
    /// Used when processing emails to ensure contact exists.
    /// </summary>
    Task<Contact> GetOrCreateContactFromEmailAsync(string emailAddress, string displayName, Guid? accountId = null);
    Task<Contact> CreateContactAsync(Contact contact);
}

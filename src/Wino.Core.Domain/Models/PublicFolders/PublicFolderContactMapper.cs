using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.MenuItems;

namespace Wino.Core.Domain.Models.PublicFolders;

/// <summary>
/// Shapes the transient contacts of a public contact folder into the contact card People lists. The cards are
/// never stored: they belong to no address book, and their id is derived from the account, the folder and
/// the server item, so a contact keeps its identity (and the selection) across reloads of the same folder.
/// </summary>
public static class PublicFolderContactMapper
{
    public static AccountContact ToAccountContact(PublicFolderContact contact, Guid accountId, string folderId)
    {
        ArgumentNullException.ThrowIfNull(contact);

        var id = PublicFolderMenuItemFactory.DeterministicId(accountId, "public-contact:" + folderId + ":" + (contact.RemoteId ?? contact.Address ?? contact.DisplayName));
        var displayName = FirstNonEmpty(contact.DisplayName, contact.Company, contact.Address);

        var card = new AccountContact
        {
            Id = id,
            MailAccountId = accountId,
            AddressBookId = Guid.Empty,
            SourceKind = ContactSourceKind.Exchange,
            RemoteId = contact.RemoteId,
            DisplayName = displayName,
            CompanyName = NullIfEmpty(contact.Company),
            JobTitle = NullIfEmpty(contact.Title),
            Notes = NullIfEmpty(contact.Notes),
            SortKey = displayName,
            IsAutoCollected = false
        };

        if (!string.IsNullOrWhiteSpace(contact.Address))
        {
            card.EmailAddresses.Add(new ContactEmailAddress
            {
                ContactId = id,
                Address = contact.Address.Trim(),
                NormalizedAddress = ContactEmailAddress.Normalize(contact.Address),
                IsPrimary = true
            });
        }

        AddPhone(card, contact.BusinessPhone, ContactPhoneKind.Work);
        AddPhone(card, contact.MobilePhone, ContactPhoneKind.Mobile);
        AddPhone(card, contact.HomePhone, ContactPhoneKind.Home);

        if (!string.IsNullOrWhiteSpace(contact.StreetAddress))
        {
            card.PostalAddresses.Add(new ContactPostalAddress
            {
                ContactId = id,
                Kind = ContactPostalAddressKind.Business,
                Street = contact.StreetAddress.Trim()
            });
        }

        return card;
    }

    /// <summary>The cards of a folder in display order, optionally narrowed to those matching a search text.</summary>
    public static List<AccountContact> ToAccountContacts(IEnumerable<PublicFolderContact> contacts, Guid accountId, string folderId, string searchQuery = null)
    {
        var search = searchQuery?.Trim();

        return (contacts ?? Array.Empty<PublicFolderContact>())
            .Where(contact => contact != null && Matches(contact, search))
            .Select(contact => ToAccountContact(contact, accountId, folderId))
            .GroupBy(card => card.Id)
            .Select(group => group.First())
            .OrderBy(card => card.SortKey, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool Matches(PublicFolderContact contact, string search)
        => string.IsNullOrEmpty(search) ||
           Contains(contact.DisplayName, search) ||
           Contains(contact.Address, search) ||
           Contains(contact.Company, search) ||
           Contains(contact.BusinessPhone, search) ||
           Contains(contact.MobilePhone, search);

    private static bool Contains(string value, string search)
        => !string.IsNullOrEmpty(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static void AddPhone(AccountContact card, string number, ContactPhoneKind kind)
    {
        if (string.IsNullOrWhiteSpace(number))
            return;

        card.PhoneNumbers.Add(new ContactPhoneNumber
        {
            ContactId = card.Id,
            Number = number.Trim(),
            Kind = kind,
            Order = card.PhoneNumbers.Count,
            IsPrimary = card.PhoneNumbers.Count == 0
        });
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

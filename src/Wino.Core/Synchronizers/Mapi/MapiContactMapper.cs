#nullable enable annotations
using System;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mapi;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>A contacts-folder row to the local contact, and a local contact to the fields written back.</summary>
internal static class MapiContactMapper
{
    /// <summary>The photo key stamped on a row that carries an attachment; the photo is pulled once per row, not per sync.</summary>
    internal const string PhotoKey = "mapi-photo";

    internal static AccountContact ToAccountContact(MapiContactInfo row, string remoteId, Guid accountId, Guid addressBookId)
    {
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            AddressBookId = addressBookId,
            SourceKind = ContactSourceKind.Exchange,
            RemoteId = remoteId,
            RemotePhotoKey = row.HasAttachments ? PhotoKey : null,
            DisplayName = FirstNonEmpty(row.DisplayName, Join(" ", row.GivenName, row.Surname), row.Company),
            GivenName = Clean(row.GivenName),
            Surname = Clean(row.Surname),
            CompanyName = Clean(row.Company),
            JobTitle = Clean(row.JobTitle),
            Notes = Clean(row.Notes),
            PendingMutation = ContactPendingMutation.None
        };

        contact.EmailAddresses = new[] { row.Email1, row.Email2, row.Email3 }
            .Select(Clean)
            .Where(e => e != null)
            .Select((address, index) => new ContactEmailAddress
            {
                Id = Guid.NewGuid(),
                ContactId = contact.Id,
                Address = address,
                NormalizedAddress = ContactEmailAddress.Normalize(address),
                Order = index,
                IsPrimary = index == 0
            })
            .ToList();

        AddPhone(contact, row.BusinessPhone, ContactPhoneKind.Work);
        AddPhone(contact, row.HomePhone, ContactPhoneKind.Home);
        AddPhone(contact, row.MobilePhone, ContactPhoneKind.Mobile);

        if (new[] { row.WorkStreet, row.WorkCity, row.WorkState, row.WorkPostalCode, row.WorkCountry }.Any(p => Clean(p) != null))
        {
            contact.PostalAddresses.Add(new ContactPostalAddress
            {
                Id = Guid.NewGuid(),
                ContactId = contact.Id,
                Kind = ContactPostalAddressKind.Business,
                Street = Clean(row.WorkStreet),
                City = Clean(row.WorkCity),
                Region = Clean(row.WorkState),
                PostalCode = Clean(row.WorkPostalCode),
                Country = Clean(row.WorkCountry)
            });
        }

        return contact;
    }

    internal static MapiContactWrite ToWrite(AccountContact item) => new()
    {
        DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.PrimaryEmailAddress : item.DisplayName,
        Company = item.CompanyName,
        JobTitle = item.JobTitle,
        BusinessPhone = Phone(item, ContactPhoneKind.Work),
        HomePhone = Phone(item, ContactPhoneKind.Home),
        MobilePhone = Phone(item, ContactPhoneKind.Mobile),
        Email = item.PrimaryEmailAddress,
        Notes = item.Notes
    };

    private static void AddPhone(AccountContact contact, string? number, ContactPhoneKind kind)
    {
        if (Clean(number) is not { } cleaned)
            return;

        contact.PhoneNumbers.Add(new ContactPhoneNumber
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            Number = cleaned,
            Kind = kind,
            Order = contact.PhoneNumbers.Count,
            IsPrimary = contact.PhoneNumbers.Count == 0
        });
    }

    private static string? Phone(AccountContact item, ContactPhoneKind kind)
        => item.PhoneNumbers?.Where(p => p.Kind == kind).OrderBy(p => p.Order).Select(p => Clean(p.Number)).FirstOrDefault(p => p != null);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values) => values.Select(Clean).FirstOrDefault(v => v != null);

    /// <summary>Joins the non-empty parts; null when none, so an empty name does not become a lone space.</summary>
    private static string? Join(string separator, params string?[] parts)
    {
        var kept = parts.Select(Clean).Where(p => p != null).ToList();
        return kept.Count == 0 ? null : string.Join(separator, kept);
    }
}

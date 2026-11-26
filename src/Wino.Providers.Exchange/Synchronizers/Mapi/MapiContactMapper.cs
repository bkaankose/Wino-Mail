using System;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Providers.Exchange.MAPI;
using Wino.Providers.Exchange.Synchronizers.Exchange;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

/// <summary>A contacts-folder row to the local contact, and a local contact to the fields written back.</summary>
internal static class MapiContactMapper
{
    /// <summary>The photo key stamped on a row that has a contact photo; the photo is pulled once per row, not per sync.</summary>
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
            RemotePhotoKey = row.HasPicture ? PhotoKey : null,
            DisplayName = FirstNonEmpty(row.DisplayName, Join(" ", row.GivenName, row.MiddleName, row.Surname), row.Company),
            HonorificPrefix = Clean(row.Prefix),
            GivenName = Clean(row.GivenName),
            MiddleName = Clean(row.MiddleName),
            Surname = Clean(row.Surname),
            HonorificSuffix = Clean(row.Generation),
            Nickname = Clean(row.Nickname),
            FileAs = Clean(row.FileUnder),
            CompanyName = Clean(row.Company),
            Department = Clean(row.Department),
            JobTitle = Clean(row.JobTitle),
            OfficeLocation = Clean(row.OfficeLocation),
            Website = Clean(row.Website),
            Notes = Clean(row.Notes),
            CategoryNames = (row.Categories ?? []).Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PendingMutation = ContactPendingMutation.None
        };

        ExchangeContactBirthday.Apply(contact, ExchangeContactBirthday.Read(row.BirthdayLocal, row.Birthday));

        contact.EmailAddresses = row.Emails
            .Select(Clean)
            .OfType<string>()
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

        AddPhone(contact, row.MobilePhone, ContactPhoneKind.Mobile);
        AddPhone(contact, row.BusinessPhone, ContactPhoneKind.Work);
        AddPhone(contact, row.BusinessPhone2, ContactPhoneKind.Work);
        AddPhone(contact, row.HomePhone, ContactPhoneKind.Home);
        AddPhone(contact, row.HomePhone2, ContactPhoneKind.Home);

        AddAddress(contact, row.HomeAddress, ContactPostalAddressKind.Home);
        AddAddress(contact, row.WorkAddress, ContactPostalAddressKind.Business);
        AddAddress(contact, row.OtherAddress, ContactPostalAddressKind.Other);

        return contact;
    }

    /// <summary>
    /// The fields written back. <paramref name="original"/> is the contact before the edit (null for a new one); its
    /// e-mail slots let an unchanged slot keep the server's entry. Categories are written on create only: an update
    /// leaves them to the category request, which carries them explicitly.
    /// </summary>
    internal static MapiContactWrite ToWrite(AccountContact item, AccountContact? original)
    {
        var work = ExchangeContactFields.Phones(item, ContactPhoneKind.Work, 2);
        var home = ExchangeContactFields.Phones(item, ContactPhoneKind.Home, 2);
        var birthday = ExchangeContactBirthday.FromContact(item);

        return new MapiContactWrite
        {
            DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.StructuredName : item.DisplayName,
            Prefix = item.HonorificPrefix,
            GivenName = item.GivenName,
            MiddleName = item.MiddleName,
            Surname = item.Surname,
            Generation = item.HonorificSuffix,
            Nickname = item.Nickname,
            Company = item.CompanyName,
            Department = item.Department,
            JobTitle = item.JobTitle,
            OfficeLocation = item.OfficeLocation,
            Website = item.Website,
            BusinessPhone = work[0],
            BusinessPhone2 = work[1],
            HomePhone = home[0],
            HomePhone2 = home[1],
            MobilePhone = ExchangeContactFields.Phones(item, ContactPhoneKind.Mobile, 1)[0],
            Emails = ExchangeContactFields.Emails(item),
            OriginalEmails = original is null ? null : ExchangeContactFields.Emails(original),
            WorkAddress = Address(item, ContactPostalAddressKind.Business),
            HomeAddress = Address(item, ContactPostalAddressKind.Home),
            OtherAddress = Address(item, ContactPostalAddressKind.Other),
            Birthday = birthday is { } day ? (ExchangeContactBirthday.ToBirthdayUtc(day), ExchangeContactBirthday.ToBirthdayLocalRaw(day)) : null,
            Categories = original is null ? ExchangeContactFields.CategoryNames(item) : null,
            Notes = item.Notes
        };
    }

    private static MapiPostalAddress? Address(AccountContact item, ContactPostalAddressKind kind)
        => item.PostalAddresses?.FirstOrDefault(address => address.Kind == kind) is { } address
            ? new MapiPostalAddress(address.Street, address.PostOfficeBox, address.City, address.Region, address.PostalCode, address.Country)
            : null;

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

    private static void AddAddress(AccountContact contact, MapiPostalAddress? address, ContactPostalAddressKind kind)
    {
        if (address is null || address.IsEmpty)
            return;

        contact.PostalAddresses.Add(new ContactPostalAddress
        {
            Id = Guid.NewGuid(),
            ContactId = contact.Id,
            Kind = kind,
            Street = Clean(address.Street),
            PostOfficeBox = Clean(address.PostOfficeBox),
            City = Clean(address.City),
            Region = Clean(address.State),
            PostalCode = Clean(address.PostalCode),
            Country = Clean(address.Country)
        });
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values) => values.Select(Clean).FirstOrDefault(v => v != null);

    /// <summary>Joins the non-empty parts; null when none, so an empty name does not become a lone space.</summary>
    private static string? Join(string separator, params string?[] parts)
    {
        var kept = parts.Select(Clean).Where(p => p != null).ToList();
        return kept.Count == 0 ? null : string.Join(separator, kept);
    }
}

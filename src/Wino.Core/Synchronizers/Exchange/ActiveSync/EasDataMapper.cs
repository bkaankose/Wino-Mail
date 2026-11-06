using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MimeKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Synchronizers.Exchange.ActiveSync;

internal static partial class EasDataMapper
{
    internal static readonly XNamespace Sync = "AirSync:";
    internal static readonly XNamespace Body = "AirSyncBase:";
    internal static readonly XNamespace Contact = "Contacts:";
    internal static readonly XNamespace Calendar = "Calendar:";
    internal static readonly XNamespace Tasks = "Tasks:";
    internal static string Value(XElement data, XNamespace ns, string name) => data.Element(ns + name)?.Value ?? string.Empty;
    internal static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    internal static string Key(Guid accountId, string folderId, string itemId) => accountId.ToString("N") + "/" + Uri.EscapeDataString(folderId) + "/" + Uri.EscapeDataString(itemId);

    internal static string ProviderId(string serverId) => "eas:" + serverId;

    internal static string ServerId(string? providerId)
        => string.IsNullOrEmpty(providerId) ? string.Empty
            : providerId.StartsWith("eas:", StringComparison.Ordinal) ? providerId[4..]
            : throw new InvalidOperationException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncItemRequiresSync);

    internal static XElement ChangedFields(XElement desired, XElement original) => new(desired.Name,
        desired.Elements().Where(element => !XNode.DeepEquals(element, original.Element(element.Name))).Select(element => new XElement(element)));

    internal static XElement WriteFlag(bool isFlagged)
    {
        XNamespace email = "Email:";
        var flag = new XElement(email + "Flag", new XElement(email + "Status", isFlagged ? "2" : "0"));
        if (isFlagged)
            flag.Add(new XElement(email + "FlagType", "Flag for follow up"), new XElement(Tasks + "StartDate"),
                new XElement(Tasks + "UtcStartDate"), new XElement(Tasks + "DueDate"), new XElement(Tasks + "UtcDueDate"));
        return new XElement(Sync + "ApplicationData", flag);
    }

    internal static DateTime? Date(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (DateTimeOffset.TryParseExact(text, new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'" },
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.UtcDateTime;

        throw new InvalidDataException("The EAS server returned an invalid timestamp.");
    }

    internal static XElement TextBody(string text) => new(Body + "Body", new XElement(Body + "Type", "1"), new XElement(Body + "Data", text ?? string.Empty));
    internal static string BodyText(XElement data)
    {
        var text = data.Element(Body + "Body")?.Element(Body + "Data");
        return text == null ? string.Empty : text.Attribute(EasWbxml.Opaque) == null ? text.Value
            : new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text.Value));
    }

    internal static MimeMessage ReadMime(XElement data)
    {
        var body = data.Element(Body + "Body") ?? throw new InvalidDataException("The EAS item has no MIME body.");
        if (Value(body, Body, "Type") != "4" || Value(body, Body, "Truncated") == "1")
            throw new InvalidDataException("The EAS server did not return a complete MIME message.");

        var content = body.Element(Body + "Data") ?? throw new InvalidDataException("The EAS MIME body is empty.");
        using var stream = new MemoryStream(content.Attribute(EasWbxml.Opaque) != null ? Convert.FromBase64String(content.Value) : Encoding.UTF8.GetBytes(content.Value));
        return MimeMessage.Load(stream);
    }

    internal static NewMailItemPackage Mail(MimeMessage message, MailItemFolder folder, string remoteId, XElement data)
    {
        XNamespace email = "Email:";
        var sender = message.From.Mailboxes.FirstOrDefault() ?? message.Sender;
        var key = Key(folder.MailAccountId, folder.RemoteFolderId, remoteId);
        var copy = new MailCopy
        {
            UniqueId = StableId(key), Id = key, FolderId = folder.Id, AssignedFolder = folder,
            FileId = StableId(key + "/mime"), Subject = message.Subject ?? string.Empty,
            FromAddress = sender?.Address ?? string.Empty, FromName = sender?.Name ?? sender?.Address ?? string.Empty,
            MessageId = message.MessageId ?? string.Empty, ThreadId = message.References.FirstOrDefault() ?? message.InReplyTo ?? message.MessageId ?? key,
            References = string.Join(';', message.References), InReplyTo = message.InReplyTo ?? string.Empty,
            CreationDate = Date(Value(data, email, "DateReceived")) ?? (message.Date == default ? DateTime.UtcNow : message.Date.UtcDateTime),
            PreviewText = message.TextBody ?? message.Subject ?? string.Empty, IsRead = Value(data, email, "Read") == "1",
            IsFlagged = Value(data.Element(email + "Flag") ?? new XElement("Empty"), email, "Status") == "2",
            HasAttachments = message.Attachments.Any(), IsDraft = folder.SpecialFolderType == SpecialFolderType.Draft,
            Importance = Value(data, email, "Importance") switch { "2" => MailImportance.High, "0" => MailImportance.Low, _ => MailImportance.Normal }
        };
        return new NewMailItemPackage(copy, message, folder.RemoteFolderId);
    }

    internal static AccountContact ReadContact(XElement data, Guid accountId, Guid bookId, string remoteId)
    {
        var result = new AccountContact
        {
            Id = StableId(Key(accountId, bookId.ToString("N"), remoteId)), MailAccountId = accountId, AddressBookId = bookId,
            SourceKind = ContactSourceKind.Exchange, RemoteId = remoteId,
            GivenName = Value(data, Contact, "FirstName"), MiddleName = Value(data, Contact, "MiddleName"), Surname = Value(data, Contact, "LastName"),
            HonorificPrefix = Value(data, Contact, "Title"), HonorificSuffix = Value(data, Contact, "Suffix"),
            FileAs = Value(data, Contact, "FileAs"), DisplayName = Value(data, Contact, "FileAs"),
            CompanyName = Value(data, Contact, "CompanyName"), Department = Value(data, Contact, "Department"),
            JobTitle = Value(data, Contact, "JobTitle"), OfficeLocation = Value(data, Contact, "OfficeLocation"),
            Website = Value(data, Contact, "WebPage"), Notes = BodyText(data),
            CategoryNames = data.Element(Contact + "Categories")?.Elements(Contact + "Category").Select(element => element.Value).ToList() ?? []
        };
        if (string.IsNullOrWhiteSpace(result.DisplayName))
            result.DisplayName = result.StructuredName;

        for (var index = 1; index <= 3; index++)
        {
            var address = Value(data, Contact, "Email" + index + "Address");
            if (!string.IsNullOrWhiteSpace(address))
            {
                var parsed = MailboxAddress.TryParse(address, out var mailbox) ? mailbox.Address : address;
                result.EmailAddresses.Add(new ContactEmailAddress { ContactId = result.Id, Address = parsed,
                    NormalizedAddress = ContactEmailAddress.Normalize(parsed), Order = index - 1, IsPrimary = index == 1 });
            }
        }

        foreach (var (tag, kind) in new[] { ("MobilePhoneNumber", ContactPhoneKind.Mobile), ("BusinessPhoneNumber", ContactPhoneKind.Work),
            ("Business2PhoneNumber", ContactPhoneKind.Work), ("HomePhoneNumber", ContactPhoneKind.Home), ("Home2PhoneNumber", ContactPhoneKind.Home) })
        {
            var number = Value(data, Contact, tag);
            if (!string.IsNullOrWhiteSpace(number))
                result.PhoneNumbers.Add(new ContactPhoneNumber { ContactId = result.Id, Number = number, Kind = kind, Order = result.PhoneNumbers.Count });
        }

        foreach (var (prefix, kind) in new[] { ("Home", ContactPostalAddressKind.Home), ("Business", ContactPostalAddressKind.Business), ("Other", ContactPostalAddressKind.Other) })
        {
            var address = new ContactPostalAddress { ContactId = result.Id, Kind = kind, Street = Value(data, Contact, prefix + "AddressStreet"),
                City = Value(data, Contact, prefix + "AddressCity"), Region = Value(data, Contact, prefix + "AddressState"),
                PostalCode = Value(data, Contact, prefix + "AddressPostalCode"), Country = Value(data, Contact, prefix + "AddressCountry") };
            if (new[] { address.Street, address.City, address.Region, address.PostalCode, address.Country }.Any(value => !string.IsNullOrEmpty(value)))
                result.PostalAddresses.Add(address);
        }

        var birthday = Date(Value(data, Contact, "Birthday"));
        result.BirthdayYear = birthday?.Year; result.BirthdayMonth = birthday?.Month; result.BirthdayDay = birthday?.Day;
        return result;
    }

    internal static XElement WriteContact(AccountContact item)
    {
        var data = new XElement(Sync + "ApplicationData", new XElement(Contact + "FirstName", item.GivenName ?? string.Empty),
            new XElement(Contact + "MiddleName", item.MiddleName ?? string.Empty), new XElement(Contact + "LastName", item.Surname ?? string.Empty),
            new XElement(Contact + "Title", item.HonorificPrefix ?? string.Empty), new XElement(Contact + "Suffix", item.HonorificSuffix ?? string.Empty),
            new XElement(Contact + "FileAs", item.DisplayName ?? item.StructuredName), new XElement(Contact + "CompanyName", item.CompanyName ?? string.Empty),
            new XElement(Contact + "Department", item.Department ?? string.Empty), new XElement(Contact + "JobTitle", item.JobTitle ?? string.Empty),
            new XElement(Contact + "OfficeLocation", item.OfficeLocation ?? string.Empty), new XElement(Contact + "WebPage", item.Website ?? string.Empty), TextBody(item.Notes));
        var emails = item.EmailAddresses.OrderByDescending(address => address.IsPrimary).ThenBy(address => address.Order).ToList();
        if (emails.Count > 3)
            throw new NotSupportedException("EAS contacts support at most three email addresses.");

        for (var index = 0; index < 3; index++)
            data.Add(new XElement(Contact + ("Email" + (index + 1) + "Address"), index < emails.Count ? emails[index].Address : string.Empty));

        foreach (var (kind, tags) in new[] { (ContactPhoneKind.Mobile, new[] { "MobilePhoneNumber" }),
            (ContactPhoneKind.Home, new[] { "HomePhoneNumber", "Home2PhoneNumber" }), (ContactPhoneKind.Work, new[] { "BusinessPhoneNumber", "Business2PhoneNumber" }) })
        {
            var numbers = item.PhoneNumbers.Where(number => number.Kind == kind).OrderBy(number => number.Order).ToList();
            if (numbers.Count > tags.Length)
                throw new NotSupportedException("The contact contains more phone numbers than EAS can represent.");

            for (var index = 0; index < tags.Length; index++)
                data.Add(new XElement(Contact + tags[index], index < numbers.Count ? numbers[index].Number : string.Empty));
        }

        foreach (var (prefix, kind) in new[] { ("Home", ContactPostalAddressKind.Home), ("Business", ContactPostalAddressKind.Business), ("Other", ContactPostalAddressKind.Other) })
        {
            var addresses = item.PostalAddresses.Where(address => address.Kind == kind).ToList();
            if (addresses.Count > 1 || addresses.Any(address => !string.IsNullOrEmpty(address.PostOfficeBox)))
                throw new NotSupportedException("The postal address cannot be represented by EAS without losing information.");

            var address = addresses.FirstOrDefault();
            data.Add(new XElement(Contact + (prefix + "AddressStreet"), address?.Street ?? string.Empty),
                new XElement(Contact + (prefix + "AddressCity"), address?.City ?? string.Empty), new XElement(Contact + (prefix + "AddressState"), address?.Region ?? string.Empty),
                new XElement(Contact + (prefix + "AddressPostalCode"), address?.PostalCode ?? string.Empty), new XElement(Contact + (prefix + "AddressCountry"), address?.Country ?? string.Empty));
        }

        if (item.BirthdayMonth.HasValue && item.BirthdayDay.HasValue)
            data.Add(new XElement(Contact + "Birthday", new DateTime(item.BirthdayYear ?? 1604, item.BirthdayMonth.Value, item.BirthdayDay.Value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));
        else
            data.Add(new XElement(Contact + "Birthday"));

        data.Add(new XElement(Contact + "Categories", (item.CategoryNames ?? item.Categories.Select(category => category.Name).ToList()).Select(name => new XElement(Contact + "Category", name))));
        return data;
    }

    internal static AccountTask ReadTask(XElement data, AccountTaskList list, string remoteId) => new()
    {
        Id = StableId(Key(list.MailAccountId, list.RemoteId, remoteId)), MailAccountId = list.MailAccountId, TaskListId = list.Id,
        SourceKind = TaskSourceKind.Exchange, RemoteId = remoteId, Title = Value(data, Tasks, "Subject"), Notes = BodyText(data),
        IsCompleted = Value(data, Tasks, "Complete") == "1", IsImportant = Value(data, Tasks, "Importance") == "2",
        DueDate = Date(Value(data, Tasks, "DueDate"))?.Date, CompletedAtUtc = Date(Value(data, Tasks, "DateCompleted"))
    };

    internal static XElement WriteTask(AccountTask item) => new(Sync + "ApplicationData",
        new XElement(Tasks + "Subject", item.Title ?? string.Empty), TextBody(item.Notes), new XElement(Tasks + "Complete", item.IsCompleted ? "1" : "0"),
        new XElement(Tasks + "Importance", item.IsImportant ? "2" : "1"),
        new XElement(Tasks + "DueDate", item.DueDate?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) ?? string.Empty),
        new XElement(Tasks + "UtcDueDate", item.DueDate?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) ?? string.Empty),
        new XElement(Tasks + "DateCompleted", item.IsCompleted ? (item.CompletedAtUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : string.Empty));
}

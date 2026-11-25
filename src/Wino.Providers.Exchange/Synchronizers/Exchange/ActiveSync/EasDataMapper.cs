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
using Wino.Services.Models.MailItem;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal static partial class EasDataMapper
{
    internal static readonly XNamespace Sync = "AirSync:";
    internal static readonly XNamespace Body = "AirSyncBase:";
    internal static readonly XNamespace Contact = "Contacts:";
    internal static readonly XNamespace Calendar = "Calendar:";
    internal static readonly XNamespace Tasks = "Tasks:";
    internal static readonly XNamespace Email = "Email:";
    internal static readonly XNamespace Email2 = "Email2:";
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
        // Empty Tasks date elements fail with status 6 on EAS 16.x; 14.1 and 16.1 both accept status and type alone.
        var flag = new XElement(email + "Flag", new XElement(email + "Status", isFlagged ? "2" : "0"));
        if (isFlagged)
            flag.Add(new XElement(email + "FlagType", "Flag for follow up"));
        return new XElement(Sync + "ApplicationData", flag);
    }

    internal static DateTime? Date(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (DateTimeOffset.TryParseExact(text, new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'" },
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.UtcDateTime;

        return null;
    }

    internal static DateTime? TryDate(string text)
    {
        try { return Date(text); }
        catch (InvalidDataException) { return null; }
    }

    internal static XElement TextBody(string text) => new(Body + "Body", new XElement(Body + "Type", "1"), new XElement(Body + "Data", text ?? string.Empty));
    internal static string BodyText(XElement data)
    {
        var text = data.Element(Body + "Body")?.Element(Body + "Data");
        if (text == null)
            return string.Empty;
        if (text.Attribute(EasWbxml.Opaque) == null)
            return text.Value;
        var buffer = new byte[text.Value.Length * 3 / 4 + 3];
        return Convert.TryFromBase64String(text.Value, buffer, out var length)
            ? EasWbxml.XmlSafe(new UTF8Encoding(false, false).GetString(buffer, 0, length)) : string.Empty;
    }

    internal static string SafeBodyText(XElement data)
    {
        try { return BodyText(data); }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return string.Empty;
        }
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

    /// <summary>
    /// Builds the message row from the Email, Email2 and AirSyncBase fields of a Sync item, as the EWS synchronizer
    /// builds it from item properties. The MIME is not part of synchronization; it is fetched when the message opens.
    /// </summary>
    internal static NewMailItemPackage Mail(MailItemFolder folder, string remoteId, XElement data)
    {
        var key = Key(folder.MailAccountId, folder.RemoteFolderId, remoteId);
        var copy = new MailCopy
        {
            UniqueId = StableId(key), Id = key, FolderId = folder.Id, AssignedFolder = folder,
            FileId = StableId(key + "/mime"), MessageId = string.Empty, References = string.Empty, InReplyTo = string.Empty,
            IsDraft = folder.SpecialFolderType == SpecialFolderType.Draft
        };
        ApplyMailFields(copy, data);
        // DateReceived is optional ([MS-ASEMAIL] 2.2.2.24); a new message without one is dated when it reached Wino.
        if (copy.CreationDate == default)
            copy.CreationDate = DateTime.UtcNow;
        return new NewMailItemPackage(copy, null, folder.RemoteFolderId);
    }

    /// <summary>Copies the fields a Sync item carries onto a message row; fields the item does not carry are left as they are.</summary>
    internal static void ApplyMailFields(MailCopy copy, XElement data)
    {
        if (data.Element(Email + "Subject") is XElement subject)
            copy.Subject = subject.Value;
        if (data.Element(Email + "From") is XElement from)
        {
            var sender = InternetAddressList.TryParse(from.Value, out var list) ? list.Mailboxes.FirstOrDefault() : null;
            copy.FromAddress = sender?.Address ?? from.Value;
            copy.FromName = string.IsNullOrWhiteSpace(sender?.Name) ? copy.FromAddress : sender.Name;
        }
        if (Date(Value(data, Email, "DateReceived")) is { } received)
            copy.CreationDate = received;
        if (data.Element(Email + "Read") != null)
            copy.IsRead = Value(data, Email, "Read") == "1";
        if (data.Element(Email + "Flag") is XElement flag)
            copy.IsFlagged = Value(flag, Email, "Status") == "2";
        if (data.Element(Email + "Importance") != null)
            copy.Importance = Value(data, Email, "Importance") switch { "2" => MailImportance.High, "0" => MailImportance.Low, _ => MailImportance.Normal };
        if (data.Element(Email + "MessageClass") is XElement messageClass)
            copy.ItemType = ExchangeSynchronizer.MapItemType(messageClass.Value);
        if (data.Element(Email2 + "ConversationId") is XElement conversation && !string.IsNullOrEmpty(conversation.Value))
            copy.ThreadId = conversation.Value;
        if (data.Element(Body + "Attachments") is XElement attachments)
            copy.HasAttachments = attachments.Elements(Body + "Attachment").Any(attachment => Value(attachment, Body, "IsInline") != "1");
        if (data.Element(Body + "Body") is XElement body)
        {
            var preview = body.Element(Body + "Preview")?.Value ?? (Value(body, Body, "Type") == "1" ? body.Element(Body + "Data")?.Value : null);
            if (preview != null)
                copy.PreviewText = Preview(preview);
        }
    }

    private static string Preview(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, 256));
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
                builder.Append(' ');
            space = false;
            builder.Append(character);
            if (builder.Length >= 255)
                break;
        }
        return builder.ToString();
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

        var birthday = ReadBirthday(Value(data, Contact, "Birthday"));
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
        // EAS has three email fields, two home and two work numbers and one postal address per kind; the rest is not written.
        var emails = item.EmailAddresses.OrderByDescending(address => address.IsPrimary).ThenBy(address => address.Order).Take(3).ToList();

        for (var index = 0; index < 3; index++)
            data.Add(new XElement(Contact + ("Email" + (index + 1) + "Address"), index < emails.Count ? emails[index].Address : string.Empty));

        foreach (var (kind, tags) in new[] { (ContactPhoneKind.Mobile, new[] { "MobilePhoneNumber" }),
            (ContactPhoneKind.Home, new[] { "HomePhoneNumber", "Home2PhoneNumber" }), (ContactPhoneKind.Work, new[] { "BusinessPhoneNumber", "Business2PhoneNumber" }) })
        {
            var numbers = item.PhoneNumbers.Where(number => number.Kind == kind).OrderBy(number => number.Order).Take(tags.Length).ToList();

            for (var index = 0; index < tags.Length; index++)
                data.Add(new XElement(Contact + tags[index], index < numbers.Count ? numbers[index].Number : string.Empty));
        }

        foreach (var (prefix, kind) in new[] { ("Home", ContactPostalAddressKind.Home), ("Business", ContactPostalAddressKind.Business), ("Other", ContactPostalAddressKind.Other) })
        {
            var address = item.PostalAddresses.FirstOrDefault(address => address.Kind == kind);
            var street = string.IsNullOrEmpty(address?.PostOfficeBox) ? address?.Street
                : string.Join('\n', new[] { address.Street, address.PostOfficeBox }.Where(line => !string.IsNullOrEmpty(line)));
            data.Add(new XElement(Contact + (prefix + "AddressStreet"), street ?? string.Empty),
                new XElement(Contact + (prefix + "AddressCity"), address?.City ?? string.Empty), new XElement(Contact + (prefix + "AddressState"), address?.Region ?? string.Empty),
                new XElement(Contact + (prefix + "AddressPostalCode"), address?.PostalCode ?? string.Empty), new XElement(Contact + (prefix + "AddressCountry"), address?.Country ?? string.Empty));
        }

        if (item.BirthdayMonth is { } month && item.BirthdayDay is { } day && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(item.BirthdayYear ?? 1604, month))
            data.Add(new XElement(Contact + "Birthday", LocalMidnightUtc(new DateTime(item.BirthdayYear ?? 1604, month, day)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));
        else
            data.Add(new XElement(Contact + "Birthday"));

        data.Add(new XElement(Contact + "Categories", (item.CategoryNames ?? item.Categories.Select(category => category.Name).ToList()).Select(name => new XElement(Contact + "Category", name))));
        return data;
    }

    /// <summary>
    /// DueDate is the due day as a local date written with a UTC designator; UtcDueDate is the instant that day starts
    /// in the user's zone. The day is read from DueDate, or from UtcDueDate in the device's zone when only that is set.
    /// </summary>
    internal static AccountTask ReadTask(XElement data, AccountTaskList list, string remoteId) => new()
    {
        Id = StableId(Key(list.MailAccountId, list.RemoteId, remoteId)), MailAccountId = list.MailAccountId, TaskListId = list.Id,
        SourceKind = TaskSourceKind.Exchange, RemoteId = remoteId, Title = Value(data, Tasks, "Subject"), Notes = SafeBodyText(data),
        IsCompleted = Value(data, Tasks, "Complete") == "1", IsImportant = Value(data, Tasks, "Importance") == "2",
        DueDate = TryDate(Value(data, Tasks, "DueDate"))?.Date
            ?? (TryDate(Value(data, Tasks, "UtcDueDate")) is { } utcDue ? TimeZoneInfo.ConvertTimeFromUtc(utcDue, TimeZoneInfo.Local).Date : null),
        CompletedAtUtc = TryDate(Value(data, Tasks, "DateCompleted"))
    };

    /// <summary>
    /// An unset date is left out: an empty Tasks date element fails with status 6 on 16.x. <paramref name="clearDueDate"/>
    /// writes the due dates empty, which <see cref="ForTaskChange"/> turns into leaving them out of the Change, so the
    /// server drops its own.
    /// </summary>
    internal static XElement WriteTask(AccountTask item, bool clearDueDate = false)
    {
        const string format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        var due = item.DueDate?.Date;
        var data = new XElement(Sync + "ApplicationData",
            new XElement(Tasks + "Subject", item.Title ?? string.Empty), TextBody(item.Notes), new XElement(Tasks + "Complete", item.IsCompleted ? "1" : "0"),
            new XElement(Tasks + "Importance", item.IsImportant ? "2" : "1"));
        if (due is { } day)
            data.Add(new XElement(Tasks + "DueDate", day.ToString(format, CultureInfo.InvariantCulture)),
                new XElement(Tasks + "UtcDueDate", LocalMidnightUtc(day).ToString(format, CultureInfo.InvariantCulture)));
        else if (clearDueDate)
            data.Add(new XElement(Tasks + "DueDate"), new XElement(Tasks + "UtcDueDate"));
        if (item.IsCompleted)
            data.Add(new XElement(Tasks + "DateCompleted", (item.CompletedAtUtc ?? DateTime.UtcNow).ToString(format, CultureInfo.InvariantCulture)));
        return data;
    }

    /// <summary>The stored task merged with the edit, without the empty date elements a Change may not carry.</summary>
    internal static XElement ForTaskChange(XElement merged)
    {
        var result = new XElement(merged);
        result.Elements().Where(element => element.Name.Namespace == Tasks && element.Name.LocalName.Contains("Date", StringComparison.Ordinal) && element.Value.Length == 0).Remove();
        return result;
    }

    /// <summary>The instant a day starts in the device's zone, as Outlook stores date-only values.</summary>
    private static DateTime LocalMidnightUtc(DateTime day)
    {
        var midnight = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        while (TimeZoneInfo.Local.IsInvalidTime(midnight))
            midnight = midnight.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, TimeZoneInfo.Local);
    }

    /// <summary>
    /// A birthday is a day stored as an instant: midnight in the zone of whoever wrote it, sent in UTC. The day is the
    /// one nearest that instant in the device's zone, so a value written in another zone does not move a day back.
    /// </summary>
    private static DateTime? ReadBirthday(string text)
        => TryDate(text) is { } utc ? TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).AddHours(12).Date : null;
}

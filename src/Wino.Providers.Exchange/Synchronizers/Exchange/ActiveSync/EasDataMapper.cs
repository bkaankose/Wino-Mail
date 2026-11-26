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
using Wino.Core.Domain.Models.Tasks;
using Wino.Services.Models.MailItem;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal static partial class EasDataMapper
{
    internal static readonly XNamespace Sync = "AirSync:";
    internal static readonly XNamespace Body = "AirSyncBase:";
    internal static readonly XNamespace Contact = "Contacts:";
    internal static readonly XNamespace Contact2 = "Contacts2:";
    internal static readonly XNamespace Calendar = "Calendar:";
    internal static readonly XNamespace Tasks = "Tasks:";
    internal static readonly XNamespace Email = "Email:";
    internal static readonly XNamespace Email2 = "Email2:";
    internal static string Value(XElement data, XNamespace ns, string name) => data.Element(ns + name)?.Value ?? string.Empty;

    // Contact text fields the server leaves out are stored as absent, as EWS and MAPI store them.
    private static string? Optional(XElement data, XNamespace ns, string name) => NullIfEmpty(Value(data, ns, name));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    internal static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    internal static string Key(Guid accountId, string folderId, string itemId) => accountId.ToString("N") + "/" + Uri.EscapeDataString(folderId) + "/" + Uri.EscapeDataString(itemId);

    internal static string ProviderId(string serverId) => "eas:" + serverId;

    /// <summary>The collection ServerId of a stored remote id; calendars stored before they carried the prefix hold it bare.</summary>
    internal static string ServerIdOf(string? remoteId)
        => remoteId is null ? string.Empty : remoteId.StartsWith("eas:", StringComparison.Ordinal) ? remoteId[4..] : remoteId;

    internal static string ServerId(string? providerId)
        => string.IsNullOrEmpty(providerId) ? string.Empty
            : providerId.StartsWith("eas:", StringComparison.Ordinal) ? providerId[4..]
            : throw new InvalidOperationException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncItemRequiresSync);

    internal static XElement ChangedFields(XElement desired, XElement original) => new(desired.Name,
        desired.Elements().Where(element => !XNode.DeepEquals(element, original.Element(element.Name))).Select(element => new XElement(element)));

    internal static XElement WriteFlag(bool isFlagged) => new(Sync + "ApplicationData", Flag(isFlagged));

    /// <summary>
    /// The message's category names, or null when the item does not carry Categories: in a Change that means they did
    /// not change ([MS-ASEMAIL] 2.2.2.16).
    /// </summary>
    internal static List<string>? MailCategories(XElement data)
        => data.Element(Email + "Categories") is XElement categories ? categories.Elements(Email + "Category").Select(category => category.Value).ToList() : null;

    /// <summary>A Change of the message's categories alone; an empty Categories element removes them all.</summary>
    internal static XElement WriteMailCategories(IEnumerable<string> names)
        => new(Sync + "ApplicationData", new XElement(Email + "Categories", names.Select(name => new XElement(Email + "Category", name))));

    /// <summary>
    /// A follow-up flag for today, with the start and due dates the flag action requires, or the empty Flag that clears
    /// it. StartDate and DueDate are the local day written with a UTC designator, their Utc forms the instant that day
    /// starts in the user's zone, as Outlook writes them.
    /// </summary>
    internal static XElement Flag(bool isFlagged)
    {
        if (!isFlagged)
            return new XElement(Email + "Flag");
        const string format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        var day = DateTime.Today;
        var local = day.ToString(format, CultureInfo.InvariantCulture);
        var utc = LocalMidnightUtc(day).ToString(format, CultureInfo.InvariantCulture);
        return new XElement(Email + "Flag", new XElement(Email + "Status", "2"), new XElement(Email + "FlagType", "Flag for follow up"),
            new XElement(Tasks + "StartDate", local), new XElement(Tasks + "UtcStartDate", utc),
            new XElement(Tasks + "DueDate", local), new XElement(Tasks + "UtcDueDate", utc));
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

    internal static XElement HtmlBody(string html) => new(Body + "Body", new XElement(Body + "Type", "2"), new XElement(Body + "Data", html ?? string.Empty));
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
            GivenName = Optional(data, Contact, "FirstName"), MiddleName = Optional(data, Contact, "MiddleName"), Surname = Optional(data, Contact, "LastName"),
            HonorificPrefix = Optional(data, Contact, "Title"), HonorificSuffix = Optional(data, Contact, "Suffix"),
            Nickname = Optional(data, Contact2, "NickName"), FileAs = Optional(data, Contact, "FileAs"),
            CompanyName = Optional(data, Contact, "CompanyName"), Department = Optional(data, Contact, "Department"),
            JobTitle = Optional(data, Contact, "JobTitle"), OfficeLocation = Optional(data, Contact, "OfficeLocation"),
            Website = Optional(data, Contact, "WebPage"), Notes = NullIfEmpty(BodyText(data)),
            CategoryNames = data.Element(Contact + "Categories")?.Elements(Contact + "Category").Select(element => element.Value).ToList() ?? []
        };
        // ActiveSync has no display name element; Exchange composes PidTagDisplayName from the name parts
        // ([MS-OXOCNTC] 2.2.1.1.8), which EWS reports, so the same parts are composed here.
        result.DisplayName = string.IsNullOrWhiteSpace(result.StructuredName) ? result.FileAs : result.StructuredName;

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

        // [MS-OXOCNTC] 2.2.1.5.1: the birthday is the day at 11:59 UTC, so the UTC calendar date is the day.
        var birthday = TryDate(Value(data, Contact, "Birthday"));
        result.BirthdayYear = birthday?.Year; result.BirthdayMonth = birthday?.Month; result.BirthdayDay = birthday?.Day;
        return result;
    }

    /// <summary>
    /// The contact's fields for a Sync Add or Change. Contacts are synchronized with Birthday as the only element
    /// that is not ghosted (see <see cref="EasClient"/>): a contact without a birthday leaves the element out, because an
    /// empty dateTime is invalid (status 6), and a Change that leaves it out deletes it on the server.
    /// </summary>
    internal static XElement WriteContact(AccountContact item)
    {
        var fileAs = !string.IsNullOrWhiteSpace(item.FileAs) ? item.FileAs : !string.IsNullOrWhiteSpace(item.DisplayName) ? item.DisplayName : item.StructuredName;
        var data = new XElement(Sync + "ApplicationData", new XElement(Contact + "FirstName", item.GivenName ?? string.Empty),
            new XElement(Contact + "MiddleName", item.MiddleName ?? string.Empty), new XElement(Contact + "LastName", item.Surname ?? string.Empty),
            new XElement(Contact + "Title", item.HonorificPrefix ?? string.Empty), new XElement(Contact + "Suffix", item.HonorificSuffix ?? string.Empty),
            new XElement(Contact2 + "NickName", item.Nickname ?? string.Empty),
            new XElement(Contact + "FileAs", fileAs), new XElement(Contact + "CompanyName", item.CompanyName ?? string.Empty),
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

        if (Birthday(item) is { } birthday)
            data.Add(birthday);

        data.Add(new XElement(Contact + "Categories", (item.CategoryNames ?? item.Categories.Select(category => category.Name).ToList()).Select(name => new XElement(Contact + "Category", name))));
        return data;
    }

    /// <summary>The contact's Birthday element ([MS-OXOCNTC] 2.2.1.5.1: the day at 11:59 UTC), or null when it has none.</summary>
    internal static XElement? Birthday(AccountContact item)
    {
        if (item.BirthdayMonth is not { } month || item.BirthdayDay is not { } day || month is < 1 or > 12)
            return null;
        // 1604 is the year Outlook stores for a birthday without a year.
        var year = item.BirthdayYear is >= 1 and <= 9999 ? item.BirthdayYear.Value : 1604;
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;
        return new XElement(Contact + "Birthday", new DateTime(year, month, day, 11, 59, 0, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The fields of a contact Change: those that differ from the stored contact, and the birthday whenever the contact
    /// has one, because Birthday is not ghosted and a Change without it deletes it.
    /// </summary>
    internal static XElement ForContactChange(AccountContact desired, AccountContact original)
    {
        var data = ChangedFields(WriteContact(desired), WriteContact(original));
        data.Elements(Contact + "Birthday").Remove();
        if (Birthday(desired) is { } birthday)
            data.Add(birthday);
        return data;
    }

    /// <summary>A Change that carries only <paramref name="fields"/>, with the contact's birthday kept (see <see cref="ForContactChange"/>).</summary>
    internal static XElement ContactChange(AccountContact contact, params XElement[] fields)
    {
        var data = new XElement(Sync + "ApplicationData", fields);
        if (Birthday(contact) is { } birthday)
            data.Add(birthday);
        return data;
    }

    /// <summary>
    /// DueDate and StartDate are days as a local date written with a UTC designator; UtcDueDate and UtcStartDate are
    /// the instant that day starts in the user's zone. The day is read from the local form, or from the UTC form in
    /// the device's zone when only that is set. Importance is 0 low, 1 normal, 2 high; ReminderTime is UTC and counts
    /// only while ReminderSet is 1 ([MS-ASTASK] 2.2.2).
    /// </summary>
    internal static AccountTask ReadTask(XElement data, AccountTaskList list, string remoteId)
    {
        var importance = data.Element(Tasks + "Importance")?.Value;
        var task = new AccountTask
        {
            Id = StableId(Key(list.MailAccountId, list.RemoteId, remoteId)), MailAccountId = list.MailAccountId, TaskListId = list.Id,
            SourceKind = TaskSourceKind.Exchange, RemoteId = remoteId, Title = Value(data, Tasks, "Subject"), Notes = SafeBodyText(data),
            IsCompleted = Value(data, Tasks, "Complete") == "1",
            DueDate = ReadTaskDay(data, "DueDate", "UtcDueDate"),
            StartDate = ReadTaskDay(data, "StartDate", "UtcStartDate"),
            CompletedAtUtc = TryDate(Value(data, Tasks, "DateCompleted")),
            ReminderAtUtc = Value(data, Tasks, "ReminderSet") == "1" ? TryDate(Value(data, Tasks, "ReminderTime")) : null,
            Categories = TaskFields.JoinCategories(data.Element(Tasks + "Categories")?.Elements(Tasks + "Category").Select(element => element.Value) ?? [])
        };
        TaskFields.SetPriority(task, importance switch { "2" => TaskPriority.High, "1" => TaskPriority.Normal, "0" => TaskPriority.Low, _ => TaskPriority.None });
        return task;
    }

    private static DateTime? ReadTaskDay(XElement data, string localName, string utcName)
        => TryDate(Value(data, Tasks, localName))?.Date
            ?? (TryDate(Value(data, Tasks, utcName)) is { } utc ? TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).Date : null);

    /// <summary>
    /// The task's fields. Tasks are not ghosted, so a Change carries the whole task and an element it leaves out is
    /// deleted on the server ([MS-ASCMD] 2.2.3.24). An unset value is therefore left out of an Add, where an empty
    /// dateTime fails with status 6, and written empty for a Change (<paramref name="forChange"/>), so it differs
    /// from a stored value and <see cref="ForTaskChange"/> turns it into leaving the element out.
    /// </summary>
    internal static XElement WriteTask(AccountTask item, bool forChange = false)
    {
        const string format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        var due = item.DueDate?.Date;
        var start = item.StartDate?.Date is { } startDay && (due == null || startDay <= due) ? startDay : (DateTime?)null;
        var data = new XElement(Sync + "ApplicationData",
            new XElement(Tasks + "Subject", item.Title ?? string.Empty), TextBody(item.Notes), new XElement(Tasks + "Complete", item.IsCompleted ? "1" : "0"),
            new XElement(Tasks + "Importance", TaskFields.GetEffectivePriority(item) switch { TaskPriority.High => "2", TaskPriority.Low => "0", _ => "1" }));

        void AddOrClear(params (string Name, string? Value)[] fields)
        {
            foreach (var (name, value) in fields)
            {
                if (value != null)
                    data.Add(new XElement(Tasks + name, value));
                else if (forChange)
                    data.Add(new XElement(Tasks + name));
            }
        }

        AddOrClear(("DueDate", due?.ToString(format, CultureInfo.InvariantCulture)), ("UtcDueDate", due is { } dueDay ? LocalMidnightUtc(dueDay).ToString(format, CultureInfo.InvariantCulture) : null),
            ("StartDate", start?.ToString(format, CultureInfo.InvariantCulture)), ("UtcStartDate", start is { } startValue ? LocalMidnightUtc(startValue).ToString(format, CultureInfo.InvariantCulture) : null),
            ("DateCompleted", item.IsCompleted ? (item.CompletedAtUtc ?? DateTime.UtcNow).ToString(format, CultureInfo.InvariantCulture) : null));

        data.Add(new XElement(Tasks + "ReminderSet", item.ReminderAtUtc.HasValue ? "1" : "0"));
        AddOrClear(("ReminderTime", item.ReminderAtUtc is { } reminder ? DateTime.SpecifyKind(reminder, DateTimeKind.Utc).ToString(format, CultureInfo.InvariantCulture) : null));

        var categories = TaskFields.GetCategories(item);
        if (categories.Count > 0 || forChange)
            data.Add(new XElement(Tasks + "Categories", categories.Select(name => new XElement(Tasks + "Category", name))));
        return data;
    }

    /// <summary>
    /// The stored task merged with the edit, reduced to what a Change may carry: no empty elements (an empty dateTime
    /// fails with status 6, and a field left out is deleted), and no body fields only a server response has
    /// ([MS-ASAIRS] 2.2.2.9: EstimatedDataSize, Truncated, Preview; NativeBodyType). Sending the server's EstimatedDataSize
    /// back fails with status 6 on Exchange SE (16.1). A truncated body is left out, so the server keeps the whole one.
    /// </summary>
    internal static XElement ForTaskChange(XElement merged)
    {
        var result = new XElement(merged);
        result.Elements().Where(element => element.Name.Namespace == Tasks && !element.HasElements && element.Value.Length == 0 && element.Name.LocalName != "Categories").Remove();
        result.Elements(Tasks + "Categories").Where(element => !element.HasElements).Remove();
        result.Elements(Body + "NativeBodyType").Remove();
        if (result.Element(Body + "Body") is { } body)
        {
            if (Value(body, Body, "Truncated") == "1")
                body.Remove();
            else
                body.Elements().Where(element => element.Name.LocalName is "EstimatedDataSize" or "Truncated" or "Preview").Remove();
        }
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
}

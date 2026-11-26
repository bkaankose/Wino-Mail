using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Providers.Exchange.MAPI.Wire;

namespace Wino.Providers.Exchange.MAPI;

/// <summary>The store's property tags for the contact named properties (PSETID_Address, PS_PUBLIC_STRINGS), resolved once per session.</summary>
public sealed class MapiContactTags
{
    public required uint[] EmailAddress { get; init; }
    public required uint[] EmailDisplayName { get; init; }
    public required uint[] EmailAddressType { get; init; }
    public required uint[] EmailOriginalDisplayName { get; init; }
    public required uint[] EmailOriginalEntryId { get; init; }
    public required uint FileUnder { get; init; }
    public required uint WorkStreet { get; init; }
    public required uint WorkCity { get; init; }
    public required uint WorkState { get; init; }
    public required uint WorkPostalCode { get; init; }
    public required uint WorkCountry { get; init; }
    public required uint WorkPostOfficeBox { get; init; }
    public required uint WorkAddress { get; init; }
    public required uint HomeAddress { get; init; }
    public required uint OtherAddress { get; init; }
    public required uint PostalAddressId { get; init; }
    public required uint AddressBookProviderEmailList { get; init; }
    public required uint AddressBookProviderArrayType { get; init; }
    public required uint BirthdayLocal { get; init; }
    public required uint HasPicture { get; init; }
    public required uint Keywords { get; init; }
}

/// <summary>One postal address of a contact, in the Contact Object Protocol's parts (MS-OXOCNTC 2.2.1.3).</summary>
public sealed record MapiPostalAddress(string? Street, string? PostOfficeBox, string? City, string? State, string? PostalCode, string? Country)
{
    public bool IsEmpty => new[] { Street, PostOfficeBox, City, State, PostalCode, Country }.All(string.IsNullOrWhiteSpace);
}

/// <summary>One row of a Contacts folder, as this client reads it.</summary>
public sealed record MapiContactInfo(ulong MessageId, string MessageClass)
{
    public string? DisplayName { get; init; }
    public string? Prefix { get; init; }
    public string? GivenName { get; init; }
    public string? MiddleName { get; init; }
    public string? Surname { get; init; }
    public string? Generation { get; init; }
    public string? Nickname { get; init; }
    public string? FileUnder { get; init; }
    public string? Company { get; init; }
    public string? Department { get; init; }
    public string? JobTitle { get; init; }
    public string? OfficeLocation { get; init; }
    public string? Website { get; init; }
    public string? BusinessPhone { get; init; }
    public string? BusinessPhone2 { get; init; }
    public string? HomePhone { get; init; }
    public string? HomePhone2 { get; init; }
    public string? MobilePhone { get; init; }
    public string?[] Emails { get; init; } = [null, null, null];
    public MapiPostalAddress? WorkAddress { get; init; }
    public MapiPostalAddress? HomeAddress { get; init; }
    public MapiPostalAddress? OtherAddress { get; init; }
    public DateTime? Birthday { get; init; }
    public DateTime? BirthdayLocal { get; init; }
    public string[]? Categories { get; init; }
    public string? Notes { get; init; }
    public bool HasPicture { get; init; }

    /// <summary>IPM.Contact and its subclasses; distribution lists (IPM.DistList) share the folder and are not contacts.</summary>
    public bool IsContact => MessageClass.StartsWith(PropertyTags.ContactMessageClass, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The fields this client writes to a contact: every field the contact editor offers.</summary>
public sealed class MapiContactWrite
{
    public string? DisplayName { get; set; }
    public string? Prefix { get; set; }
    public string? GivenName { get; set; }
    public string? MiddleName { get; set; }
    public string? Surname { get; set; }
    public string? Generation { get; set; }
    public string? Nickname { get; set; }
    public string? Company { get; set; }
    public string? Department { get; set; }
    public string? JobTitle { get; set; }
    public string? OfficeLocation { get; set; }
    public string? Website { get; set; }
    public string? BusinessPhone { get; set; }
    public string? BusinessPhone2 { get; set; }
    public string? HomePhone { get; set; }
    public string? HomePhone2 { get; set; }
    public string? MobilePhone { get; set; }

    /// <summary>The three e-mail slots as the contact now has them.</summary>
    public string?[] Emails { get; set; } = [null, null, null];

    /// <summary>
    /// The slots as the contact had them before the edit, or null for a new contact. A slot whose address did not
    /// change is left as the server has it, so an address-book entry stored in it (type EX) is not replaced.
    /// </summary>
    public string?[]? OriginalEmails { get; set; }

    public MapiPostalAddress? WorkAddress { get; set; }
    public MapiPostalAddress? HomeAddress { get; set; }
    public MapiPostalAddress? OtherAddress { get; set; }

    /// <summary>PidTagBirthday and PidLidBirthdayLocal as the mapper computed them; both null for no birthday.</summary>
    public (DateTime Utc, DateTime LocalRaw)? Birthday { get; set; }

    /// <summary>The categories to write, or null to leave them as they are.</summary>
    public string[]? Categories { get; set; }

    public string? Notes { get; set; }
}

/// <summary>What a contact holds on the server before an update, for the properties that must stay consistent with it.</summary>
public sealed record MapiContactState(uint? AddressBookProviderArrayType, uint? PostalAddressId);

/// <summary>
/// Contacts over MAPI: contact folders found through the hierarchy (the default one by its well-known entry id on the
/// Inbox, MS-OXOSFLD 2.2.3), rows read from a folder's contents table with the fixed contact properties plus the
/// PSETID_Address named ones, and create / update as an IPM.Contact message (MS-OXOCNTC).
/// </summary>
public static class MapiContactOperations
{
    private const int Slots = 3;
    private const int PhotoSlots = 5;

    // Contact properties not in the shared tag list (MS-OXPROPS).
    private const uint DisplayNamePrefix = 0x3A45001F;               // PidTagDisplayNamePrefix
    private const uint Generation = 0x3A05001F;                      // PidTagGeneration
    private const uint OfficeLocation = 0x3A19001F;                  // PidTagOfficeLocation
    private const uint Business2TelephoneNumber = 0x3A1B001F;        // PidTagBusiness2TelephoneNumber
    private const uint Home2TelephoneNumber = 0x3A2F001F;            // PidTagHome2TelephoneNumber
    private const uint HomeAddressPostOfficeBox = 0x3A5E001F;        // PidTagHomeAddressPostOfficeBox
    private const uint OtherAddressStreet = 0x3A63001F;              // PidTagOtherAddressStreet
    private const uint OtherAddressCity = 0x3A5F001F;                // PidTagOtherAddressCity
    private const uint OtherAddressStateOrProvince = 0x3A62001F;     // PidTagOtherAddressStateOrProvince
    private const uint OtherAddressPostalCode = 0x3A61001F;          // PidTagOtherAddressPostalCode
    private const uint OtherAddressCountry = 0x3A60001F;             // PidTagOtherAddressCountry
    private const uint OtherAddressPostOfficeBox = 0x3A64001F;       // PidTagOtherAddressPostOfficeBox
    private const uint StreetAddress = 0x3A29001F;                   // PidTagStreetAddress (mailing address)
    private const uint Locality = 0x3A27001F;                        // PidTagLocality
    private const uint StateOrProvince = 0x3A28001F;                 // PidTagStateOrProvince
    private const uint PostalCode = 0x3A2A001F;                      // PidTagPostalCode
    private const uint Country = 0x3A26001F;                         // PidTagCountry
    private const uint PostOfficeBox = 0x3A2B001F;                   // PidTagPostOfficeBox
    private const uint PostalAddress = 0x3A15001F;                   // PidTagPostalAddress
    private const uint AttachExtension = 0x3703001F;                 // PidTagAttachExtension

    // MS-OXOCNTC 2.2.1.8.3: the contact photo attachment's names.
    private const string PhotoFileName = "ContactPicture.jpg";
    private const string PhotoExtension = ".jpg";

    public static async Task<MapiContactTags> ResolveTagsAsync(MapiSession session, CancellationToken cancellationToken = default)
    {
        var address = PropertyTags.AddressPropertySet;
        var names = new List<(RopProperties.PropertyName Name, ushort Type)>
        {
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail1EmailAddress), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail2EmailAddress), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail3EmailAddress), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail1DisplayName), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, 0x8090), PropertyTypes.Unicode),                  // PidLidEmail2DisplayName
            (RopProperties.PropertyName.ById(address, 0x80A0), PropertyTypes.Unicode),                  // PidLidEmail3DisplayName
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail1AddressType), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail2AddressType), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail3AddressType), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail1OriginalDisplayName), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail2OriginalDisplayName), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidEmail3OriginalDisplayName), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, 0x8085), PropertyTypes.Binary),                   // PidLidEmail1OriginalEntryId
            (RopProperties.PropertyName.ById(address, 0x8095), PropertyTypes.Binary),                   // PidLidEmail2OriginalEntryId
            (RopProperties.PropertyName.ById(address, 0x80A5), PropertyTypes.Binary),                   // PidLidEmail3OriginalEntryId
            (RopProperties.PropertyName.ById(address, PropertyTags.LidFileUnder), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidWorkAddressStreet), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidWorkAddressCity), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidWorkAddressState), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidWorkAddressPostalCode), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, PropertyTags.LidWorkAddressCountry), PropertyTypes.Unicode),
            (RopProperties.PropertyName.ById(address, 0x804A), PropertyTypes.Unicode),                  // PidLidWorkAddressPostOfficeBox
            (RopProperties.PropertyName.ById(address, 0x801B), PropertyTypes.Unicode),                  // PidLidWorkAddress
            (RopProperties.PropertyName.ById(address, 0x801A), PropertyTypes.Unicode),                  // PidLidHomeAddress
            (RopProperties.PropertyName.ById(address, 0x801C), PropertyTypes.Unicode),                  // PidLidOtherAddress
            (RopProperties.PropertyName.ById(address, 0x8022), PropertyTypes.Long),                     // PidLidPostalAddressId
            (RopProperties.PropertyName.ById(address, 0x8028), MapiPropertyEdits.MultipleLong),        // PidLidAddressBookProviderEmailList
            (RopProperties.PropertyName.ById(address, 0x8029), PropertyTypes.Long),                     // PidLidAddressBookProviderArrayType
            (RopProperties.PropertyName.ById(address, 0x80DE), PropertyTypes.SysTime),                  // PidLidBirthdayLocal
            (RopProperties.PropertyName.ById(address, 0x8015), PropertyTypes.Boolean),                  // PidLidHasPicture
            (RopProperties.PropertyName.ByName(PropertyTags.PublicStringsPropertySet, PropertyTags.KeywordsPropertyName), PropertyTypes.MultipleUnicode),
        };

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 1);
        var (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertyIdsFromNames(0, names.Select(n => n.Name).ToList()), handles, cancellationToken).ConfigureAwait(false);
        var ids = RopProperties.ParseGetPropertyIdsFromNames(new RopReader(rops));
        if (ids.Count != names.Count || ids.Any(id => id == 0))
            throw new MapiFormatException("The store did not map every contact named property.");

        uint Tag(int i) => ((uint)ids[i] << 16) | names[i].Type;
        return new MapiContactTags
        {
            EmailAddress = [Tag(0), Tag(1), Tag(2)],
            EmailDisplayName = [Tag(3), Tag(4), Tag(5)],
            EmailAddressType = [Tag(6), Tag(7), Tag(8)],
            EmailOriginalDisplayName = [Tag(9), Tag(10), Tag(11)],
            EmailOriginalEntryId = [Tag(12), Tag(13), Tag(14)],
            FileUnder = Tag(15),
            WorkStreet = Tag(16),
            WorkCity = Tag(17),
            WorkState = Tag(18),
            WorkPostalCode = Tag(19),
            WorkCountry = Tag(20),
            WorkPostOfficeBox = Tag(21),
            WorkAddress = Tag(22),
            HomeAddress = Tag(23),
            OtherAddress = Tag(24),
            PostalAddressId = Tag(25),
            AddressBookProviderEmailList = Tag(26),
            AddressBookProviderArrayType = Tag(27),
            BirthdayLocal = Tag(28),
            HasPicture = Tag(29),
            Keywords = Tag(30),
        };
    }

    /// <summary>The default Contacts folder's id, from PidTagIpmContactEntryId on the Inbox; null when the mailbox has none.</summary>
    public static Task<ulong?> FindContactsFolderIdAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken = default)
        => FindWellKnownFolderIdAsync(session, inboxFolderId, PropertyTags.IpmContactEntryId, cancellationToken);

    /// <summary>Resolves one of the PidTagIpm*EntryId folder pointers on the Inbox to a folder id.</summary>
    public static async Task<ulong?> FindWellKnownFolderIdAsync(MapiSession session, ulong inboxFolderId, uint entryIdTag, CancellationToken cancellationToken = default)
    {
        uint[] tags = [entryIdTag];
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 2);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(inboxFolderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, 2);

        byte[]? entryId;
        try
        {
            (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(1, tags), handles, cancellationToken).ConfigureAwait(false);
            entryId = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), tags)[0].AsBinary;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        if (entryId is not { Length: 46 })
            return null;

        // Folder EntryID (MS-OXCDATA 2.2.4.1): Flags(4) ProviderUID(16) FolderType(2) then the long-term id (22) and padding.
        var longTermId = entryId.AsSpan(22, RopIds.LongTermIdLength).ToArray();
        (rops, _) = await session.ExecuteAsync(RopIds.BuildIdFromLongTermId(0, longTermId), handles, cancellationToken).ConfigureAwait(false);
        return RopIds.ParseIdFromLongTermId(new RopReader(rops));
    }

    /// <summary>
    /// The SMTP form of a contact e-mail slot. Internal recipients are stored with address type "EX"
    /// and the legacy DN as the address; Outlook keeps their SMTP address in the slot's original
    /// display name (MS-OXOCNTC 2.2.1.2.3), and failing that inside "Name (address)" display text.
    /// </summary>
    public static string? ResolveSmtpAddress(string? address, string? addressType, string? originalDisplayName, string? displayName = null)
    {
        var isDn = string.Equals(addressType, "EX", StringComparison.OrdinalIgnoreCase)
                   || (address is not null && address.TrimStart().StartsWith("/o=", StringComparison.OrdinalIgnoreCase));
        if (!isDn)
            return string.IsNullOrWhiteSpace(address) ? null : address.Trim();

        if (originalDisplayName is not null && originalDisplayName.Contains('@'))
            return originalDisplayName.Trim();

        var open = displayName?.LastIndexOf('(') ?? -1;
        var close = displayName?.LastIndexOf(')') ?? -1;
        if (displayName is not null && open >= 0 && close > open && displayName.AsSpan(open + 1, close - open - 1).Contains('@'))
            return displayName.Substring(open + 1, close - open - 1).Trim();

        return null;
    }

    private static uint[] Columns(MapiContactTags tags) =>
    [
        PropertyTags.Mid, PropertyTags.MessageClass, PropertyTags.Body,
        PropertyTags.DisplayName, DisplayNamePrefix, PropertyTags.GivenName, PropertyTags.MiddleName, PropertyTags.Surname, Generation, PropertyTags.Nickname, tags.FileUnder,
        PropertyTags.CompanyName, PropertyTags.DepartmentName, PropertyTags.JobTitle, OfficeLocation, PropertyTags.BusinessHomePage,
        PropertyTags.BusinessTelephoneNumber, Business2TelephoneNumber, PropertyTags.HomeTelephoneNumber, Home2TelephoneNumber, PropertyTags.MobileTelephoneNumber,
        .. tags.EmailAddress, .. tags.EmailAddressType, .. tags.EmailOriginalDisplayName, .. tags.EmailDisplayName,
        tags.WorkStreet, tags.WorkPostOfficeBox, tags.WorkCity, tags.WorkState, tags.WorkPostalCode, tags.WorkCountry,
        PropertyTags.HomeAddressStreet, HomeAddressPostOfficeBox, PropertyTags.HomeAddressCity, PropertyTags.HomeAddressStateOrProvince, PropertyTags.HomeAddressPostalCode, PropertyTags.HomeAddressCountry,
        OtherAddressStreet, OtherAddressPostOfficeBox, OtherAddressCity, OtherAddressStateOrProvince, OtherAddressPostalCode, OtherAddressCountry,
        PropertyTags.Birthday, tags.BirthdayLocal, tags.Keywords, tags.HasPicture,
    ];

    public static async Task<List<MapiContactInfo>> ReadContactsAsync(MapiSession session, ulong folderId, MapiContactTags tags, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var columns = Columns(tags);
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < columns.Length; i++)
            index.TryAdd(columns[i], i);

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        var replicas = RopFolder.ParseOpenFolder(new RopReader(rops));
        if (replicas.Count > 0) diagnostics?.Invoke($"folder 0x{folderId:X16} is ghosted; replicas: {string.Join(", ", replicas)}");
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopFolder.BuildGetContentsTable(1, 2, RopFolder.TableFlags.DeferredErrors), handles, cancellationToken).ConfigureAwait(false);
            var rowCount = RopFolder.ParseGetContentsTable(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, columns), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));

                var contacts = new List<MapiContactInfo>((int)Math.Min(rowCount, 4096));
                var partialNotes = new List<int>();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // About fifty string columns, one of them a body: small pages keep well inside the 32KB buffer.
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 10), handles, cancellationToken).ConfigureAwait(false);
                    List<PropertyValue[]> rows;
                    try
                    {
                        rows = RopFolder.ParseQueryRows(new RopReader(rops), columns);
                    }
                    catch (MapiRopException ex)
                    {
                        diagnostics?.Invoke($"RopQueryRows on 0x{folderId:X16} failed ({ex.Message}); response:" + Environment.NewLine + HexDump.Render(rops, 512));
                        throw replicas.Count > 0 ? new MapiGhostedFolderException(replicas, ex) : ex;
                    }
                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        if (row[0].AsUInt64 is not { } mid)
                            continue;

                        var errors = row.Where(c => c.Error is not null && c.Error != MapiRopException.NotFound).Select(c => $"{PropertyTags.Describe(c.Tag)}=0x{c.Error:X8}").ToList();
                        if (errors.Count > 0 && diagnostics is not null)
                            diagnostics($"contact 0x{mid:X16} cell errors: {string.Join(", ", errors)}");

                        if (MapiMessageOperations.MayBeTruncatedStringCell(row[index[PropertyTags.Body]]))
                            partialNotes.Add(contacts.Count);

                        contacts.Add(ToInfo(mid, row, index, tags));
                    }
                }

                // A long note comes back from the table as an error cell or cut short; it is read off the message so
                // the sync never stores (or later writes back) a partial note.
                foreach (var position in partialNotes.Where(position => contacts[position].IsContact))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var notes = await MapiMessageOperations.ReadUnicodePropertyAsync(session, folderId, contacts[position].MessageId, PropertyTags.Body, cancellationToken).ConfigureAwait(false);
                    contacts[position] = contacts[position] with { Notes = notes ?? contacts[position].Notes };
                }

                diagnostics?.Invoke($"contacts 0x{folderId:X16}: {contacts.Count} rows ({contacts.Count(c => c.IsContact)} contacts, RowCount {rowCount}, {contacts.Count(c => c.Emails[0] is not null)} with e-mail 1, {contacts.Count(c => c.HasPicture)} with a photo)");
                return contacts;
            }
            finally
            {
                await session.ReleaseAsync(handles[2], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    private static MapiContactInfo ToInfo(ulong mid, PropertyValue[] row, Dictionary<uint, int> index, MapiContactTags tags)
    {
        PropertyValue Cell(uint tag) => row[index[tag]];
        string? Text(uint tag) => Cell(tag).AsString;
        MapiPostalAddress Address(uint street, uint box, uint city, uint state, uint postal, uint country)
            => new(Text(street), Text(box), Text(city), Text(state), Text(postal), Text(country));

        var emails = new string?[3];
        for (var slot = 0; slot < 3; slot++)
        {
            emails[slot] = ResolveSmtpAddress(Text(tags.EmailAddress[slot]), Text(tags.EmailAddressType[slot]), Text(tags.EmailOriginalDisplayName[slot]), Text(tags.EmailDisplayName[slot]));
        }

        return new MapiContactInfo(mid, Text(PropertyTags.MessageClass) ?? string.Empty)
        {
            Notes = Text(PropertyTags.Body),
            DisplayName = Text(PropertyTags.DisplayName),
            Prefix = Text(DisplayNamePrefix),
            GivenName = Text(PropertyTags.GivenName),
            MiddleName = Text(PropertyTags.MiddleName),
            Surname = Text(PropertyTags.Surname),
            Generation = Text(Generation),
            Nickname = Text(PropertyTags.Nickname),
            FileUnder = Text(tags.FileUnder),
            Company = Text(PropertyTags.CompanyName),
            Department = Text(PropertyTags.DepartmentName),
            JobTitle = Text(PropertyTags.JobTitle),
            OfficeLocation = Text(OfficeLocation),
            Website = Text(PropertyTags.BusinessHomePage),
            BusinessPhone = Text(PropertyTags.BusinessTelephoneNumber),
            BusinessPhone2 = Text(Business2TelephoneNumber),
            HomePhone = Text(PropertyTags.HomeTelephoneNumber),
            HomePhone2 = Text(Home2TelephoneNumber),
            MobilePhone = Text(PropertyTags.MobileTelephoneNumber),
            Emails = emails,
            WorkAddress = Address(tags.WorkStreet, tags.WorkPostOfficeBox, tags.WorkCity, tags.WorkState, tags.WorkPostalCode, tags.WorkCountry),
            HomeAddress = Address(PropertyTags.HomeAddressStreet, HomeAddressPostOfficeBox, PropertyTags.HomeAddressCity, PropertyTags.HomeAddressStateOrProvince, PropertyTags.HomeAddressPostalCode, PropertyTags.HomeAddressCountry),
            OtherAddress = Address(OtherAddressStreet, OtherAddressPostOfficeBox, OtherAddressCity, OtherAddressStateOrProvince, OtherAddressPostalCode, OtherAddressCountry),
            Birthday = Cell(PropertyTags.Birthday).AsDateTime,
            BirthdayLocal = Cell(tags.BirthdayLocal).AsDateTime,
            Categories = Cell(tags.Keywords).Value as string[],
            HasPicture = Cell(tags.HasPicture).AsBoolean ?? false,
        };
    }

    /// <summary>Creates an IPM.Contact in the folder; returns its message id.</summary>
    public static async Task<ulong> CreateContactAsync(MapiSession session, ulong folderId, MapiContactTags tags, MapiContactWrite contact, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateMessage(0, 1, folderId), handles, cancellationToken).ConfigureAwait(false);
        var createdId = RopMessageWrite.ParseCreateMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            await MapiPropertyEdits.ApplyAsync(session, handles, 1, Properties(tags, contact, existing: null), "contact", cancellationToken).ConfigureAwait(false);
            var savedId = await SaveAsync(session, handles, cancellationToken).ConfigureAwait(false);
            return savedId != 0 ? savedId : createdId ?? 0;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Rewrites the editable fields of an existing contact.</summary>
    public static async Task UpdateContactAsync(MapiSession session, ulong folderId, ulong messageId, MapiContactTags tags, MapiContactWrite contact, CancellationToken cancellationToken = default)
    {
        var handles = await OpenForWriteAsync(session, folderId, messageId, Slots, cancellationToken).ConfigureAwait(false);
        try
        {
            uint[] stateTags = [tags.AddressBookProviderArrayType, tags.PostalAddressId];
            var (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(1, stateTags), handles, cancellationToken).ConfigureAwait(false);
            var values = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), stateTags);
            var existing = new MapiContactState(values[0].AsUInt32, values[1].AsUInt32);

            await MapiPropertyEdits.ApplyAsync(session, handles, 1, Properties(tags, contact, existing), "contact", cancellationToken).ConfigureAwait(false);
            await SaveAsync(session, handles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Makes the contact carry exactly these categories (PidNameKeywords, MS-OXCMSG 2.2.1.17).</summary>
    public static async Task SetCategoriesAsync(MapiSession session, ulong folderId, ulong messageId, MapiContactTags tags, IReadOnlyList<string> categories, CancellationToken cancellationToken = default)
    {
        var handles = await OpenForWriteAsync(session, folderId, messageId, Slots, cancellationToken).ConfigureAwait(false);
        try
        {
            var changes = new MapiPropertyEdits.Changes();
            AddCategories(changes, tags.Keywords, categories, deleteWhenEmpty: true);
            await MapiPropertyEdits.ApplyAsync(session, handles, 1, changes, "contact", cancellationToken).ConfigureAwait(false);
            await SaveAsync(session, handles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void AddCategories(MapiPropertyEdits.Changes changes, uint keywordsTag, IReadOnlyList<string> categories, bool deleteWhenEmpty)
    {
        var names = categories.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > 0)
            changes.Set.Add(new TaggedPropertyValue(keywordsTag, names));
        else if (deleteWhenEmpty)
            changes.Delete.Add(keywordsTag);
    }

    private static async Task<uint[]> OpenForWriteAsync(MapiSession session, ulong folderId, ulong messageId, int slots, CancellationToken cancellationToken)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        return MapiSession.MergeHandles(handles, returned, slots);
    }

    private static async Task<ulong> SaveAsync(MapiSession session, uint[] handles, CancellationToken cancellationToken)
    {
        var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken).ConfigureAwait(false);
        return RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
    }

    /// <summary>
    /// The contact's properties (MS-OXOCNTC 2.2.1) and the related ones its sequencing rules keep consistent (3.1.5):
    /// a field the contact no longer has is removed on an update; an e-mail slot is rewritten with its display name
    /// and type, and its original entry id is removed, which 3.1.5.3 allows; the address-book provider list follows
    /// the e-mail slots when the contact carries one (2.2.1.2.11, 2.2.1.2.12); the mailing address follows the
    /// address PidLidPostalAddressId names (2.2.1.3.9); the birthday is written as PidTagBirthday and
    /// PidLidBirthdayLocal together (3.1.5.5).
    /// </summary>
    public static MapiPropertyEdits.Changes Properties(MapiContactTags tags, MapiContactWrite contact, MapiContactState? existing)
    {
        var isNew = existing is null;
        var changes = new MapiPropertyEdits.Changes();
        var displayName = FirstNonEmpty(contact.DisplayName, Join(" ", contact.GivenName, contact.MiddleName, contact.Surname), contact.Company, contact.Emails.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))) ?? string.Empty;

        if (isNew)
            changes.Set.Add(TaggedPropertyValue.Unicode(PropertyTags.MessageClass, PropertyTags.ContactMessageClass));

        changes.Set.Add(TaggedPropertyValue.Unicode(PropertyTags.DisplayName, displayName));
        changes.Set.Add(TaggedPropertyValue.Unicode(PropertyTags.Subject, displayName));
        changes.Set.Add(TaggedPropertyValue.Unicode(tags.FileUnder, displayName));

        var clear = !isNew;
        changes.Text(DisplayNamePrefix, contact.Prefix, clear);
        changes.Text(PropertyTags.GivenName, contact.GivenName, clear);
        changes.Text(PropertyTags.MiddleName, contact.MiddleName, clear);
        changes.Text(PropertyTags.Surname, contact.Surname, clear);
        changes.Text(Generation, contact.Generation, clear);
        changes.Text(PropertyTags.Nickname, contact.Nickname, clear);
        changes.Text(PropertyTags.CompanyName, contact.Company, clear);
        changes.Text(PropertyTags.DepartmentName, contact.Department, clear);
        changes.Text(PropertyTags.JobTitle, contact.JobTitle, clear);
        changes.Text(OfficeLocation, contact.OfficeLocation, clear);
        changes.Text(PropertyTags.BusinessHomePage, contact.Website, clear);
        changes.Text(PropertyTags.BusinessTelephoneNumber, contact.BusinessPhone, clear);
        changes.Text(Business2TelephoneNumber, contact.BusinessPhone2, clear);
        changes.Text(PropertyTags.HomeTelephoneNumber, contact.HomePhone, clear);
        changes.Text(Home2TelephoneNumber, contact.HomePhone2, clear);
        changes.Text(PropertyTags.MobileTelephoneNumber, contact.MobilePhone, clear);
        changes.Text(PropertyTags.Body, contact.Notes, clear);

        AddEmails(changes, tags, contact, displayName, existing);
        AddAddresses(changes, tags, contact, existing);

        if (contact.Birthday is { } birthday)
        {
            changes.Set.Add(TaggedPropertyValue.SysTime(PropertyTags.Birthday, birthday.Utc));
            changes.Set.Add(TaggedPropertyValue.SysTime(tags.BirthdayLocal, birthday.LocalRaw));
        }
        else if (clear)
        {
            changes.Delete.Add(PropertyTags.Birthday);
            changes.Delete.Add(tags.BirthdayLocal);
        }

        if (contact.Categories is { } categories)
            AddCategories(changes, tags.Keywords, categories, deleteWhenEmpty: clear);

        return changes;
    }

    private static void AddEmails(MapiPropertyEdits.Changes changes, MapiContactTags tags, MapiContactWrite contact, string displayName, MapiContactState? existing)
    {
        var isNew = existing is null;
        for (var slot = 0; slot < 3; slot++)
        {
            var email = Clean(contact.Emails.ElementAtOrDefault(slot));
            var original = Clean(contact.OriginalEmails?.ElementAtOrDefault(slot));
            if (!isNew && contact.OriginalEmails is not null && string.Equals(email, original, StringComparison.OrdinalIgnoreCase))
                continue;

            if (email is not null)
            {
                changes.Set.Add(TaggedPropertyValue.Unicode(tags.EmailAddress[slot], email));
                changes.Set.Add(TaggedPropertyValue.Unicode(tags.EmailAddressType[slot], "SMTP"));
                changes.Set.Add(TaggedPropertyValue.Unicode(tags.EmailOriginalDisplayName[slot], email));
                changes.Set.Add(TaggedPropertyValue.Unicode(tags.EmailDisplayName[slot], $"{displayName} ({email})"));
                if (!isNew)
                    changes.Delete.Add(tags.EmailOriginalEntryId[slot]);
            }
            else if (!isNew)
            {
                changes.Delete.AddRange([tags.EmailAddress[slot], tags.EmailAddressType[slot], tags.EmailOriginalDisplayName[slot], tags.EmailDisplayName[slot], tags.EmailOriginalEntryId[slot]]);
            }
        }

        // The provider list also covers the fax entries (bits 0x08, 0x10, 0x20), which this client does not edit, so
        // those bits are kept as the server has them. A contact that never carried the list is left without one.
        if (!isNew && existing?.AddressBookProviderArrayType is null)
            return;

        var bits = (existing?.AddressBookProviderArrayType ?? 0) & ~0x7u;
        for (var slot = 0; slot < 3; slot++)
        {
            if (Clean(contact.Emails.ElementAtOrDefault(slot)) is not null)
                bits |= 1u << slot;
        }

        if (bits == 0)
        {
            if (!isNew)
                changes.Delete.AddRange([tags.AddressBookProviderArrayType, tags.AddressBookProviderEmailList]);
            return;
        }

        var list = Enumerable.Range(0, 6).Where(value => (bits & (1u << value)) != 0).Select(value => (uint)value).ToArray();
        changes.Set.Add(TaggedPropertyValue.Long(tags.AddressBookProviderArrayType, bits));
        changes.SetMultipleLong.Add((tags.AddressBookProviderEmailList, list));
    }

    private static void AddAddresses(MapiPropertyEdits.Changes changes, MapiContactTags tags, MapiContactWrite contact, MapiContactState? existing)
    {
        var clear = existing is not null;
        void Address(MapiPostalAddress? address, uint street, uint box, uint city, uint state, uint postal, uint country, uint composite)
        {
            changes.Text(street, address?.Street, clear);
            changes.Text(box, address?.PostOfficeBox, clear);
            changes.Text(city, address?.City, clear);
            changes.Text(state, address?.State, clear);
            changes.Text(postal, address?.PostalCode, clear);
            changes.Text(country, address?.Country, clear);
            changes.Text(composite, Compose(address), clear);
        }

        Address(contact.WorkAddress, tags.WorkStreet, tags.WorkPostOfficeBox, tags.WorkCity, tags.WorkState, tags.WorkPostalCode, tags.WorkCountry, tags.WorkAddress);
        Address(contact.HomeAddress, PropertyTags.HomeAddressStreet, HomeAddressPostOfficeBox, PropertyTags.HomeAddressCity, PropertyTags.HomeAddressStateOrProvince, PropertyTags.HomeAddressPostalCode, PropertyTags.HomeAddressCountry, tags.HomeAddress);
        Address(contact.OtherAddress, OtherAddressStreet, OtherAddressPostOfficeBox, OtherAddressCity, OtherAddressStateOrProvince, OtherAddressPostalCode, OtherAddressCountry, tags.OtherAddress);

        // 2.2.1.3.9: 1 home, 2 work, 3 other is the mailing address and the mailing properties equal it; with 0 (or
        // none) they are not set. A new contact gets no mailing address, as the property is optional.
        if (existing?.PostalAddressId is not (1 or 2 or 3))
            return;

        var mailing = existing.PostalAddressId switch
        {
            1 => contact.HomeAddress,
            2 => contact.WorkAddress,
            _ => contact.OtherAddress
        };

        if (mailing is null || mailing.IsEmpty)
        {
            changes.Set.Add(TaggedPropertyValue.Long(tags.PostalAddressId, 0));
            changes.Delete.AddRange([StreetAddress, PostOfficeBox, Locality, StateOrProvince, PostalCode, Country, PostalAddress]);
            return;
        }

        changes.Text(StreetAddress, mailing.Street, true);
        changes.Text(PostOfficeBox, mailing.PostOfficeBox, true);
        changes.Text(Locality, mailing.City, true);
        changes.Text(StateOrProvince, mailing.State, true);
        changes.Text(PostalCode, mailing.PostalCode, true);
        changes.Text(Country, mailing.Country, true);
        changes.Text(PostalAddress, Compose(mailing), true);
    }

    /// <summary>The complete address text (2.2.1.3.8): street, post office box, "city state postal code", country, one per line.</summary>
    private static string? Compose(MapiPostalAddress? address)
    {
        if (address is null || address.IsEmpty)
            return null;

        var lines = new[] { Clean(address.Street), Clean(address.PostOfficeBox), Join(" ", address.City, address.State, address.PostalCode), Clean(address.Country) };
        return string.Join("\r\n", lines.Where(line => line is not null));
    }

    /// <summary>
    /// The contact's photo: the attachment marked with PidTagAttachmentContactPhoto (MS-OXOCNTC 2.2.1.8.2), or null
    /// when it has none.
    /// </summary>
    public static async Task<byte[]?> ReadContactPhotoAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], PhotoSlots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, PhotoSlots);

        try
        {
            var photos = await FindPhotoAttachmentsAsync(session, handles, cancellationToken).ConfigureAwait(false);
            if (photos.Count == 0)
                return null;

            (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildOpenAttachment(1, 3, photos[0]), handles, cancellationToken).ConfigureAwait(false);
            RopMessageOps.ParseOpenAttachment(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, PhotoSlots);

            try
            {
                uint[] tags = [PropertyTags.AttachDataBinary];
                (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(3, tags), handles, cancellationToken).ConfigureAwait(false);
                var cell = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), tags)[0];
                if (cell.AsBinary is { } inline)
                    return inline;

                return cell.Error == MapiRopException.NotEnoughMemory
                    ? await ReadStreamAsync(session, handles, 3, 4, PropertyTags.AttachDataBinary, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            finally
            {
                await session.ReleaseAsync(handles[3], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces the contact's photo, or removes it when <paramref name="photo"/> is null. The photo attachment carries
    /// the properties MS-OXOCNTC 2.2.1.8.3 lists, and PidLidHasPicture is updated with it (3.1.5.7).
    /// </summary>
    public static async Task SetContactPhotoAsync(MapiSession session, ulong folderId, ulong messageId, MapiContactTags tags, byte[]? photo, CancellationToken cancellationToken = default)
    {
        var handles = await OpenForWriteAsync(session, folderId, messageId, PhotoSlots, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var number in await FindPhotoAttachmentsAsync(session, handles, cancellationToken).ConfigureAwait(false))
            {
                var (deleted, _) = await session.ExecuteAsync(RopMessageWrite.BuildDeleteAttachment(1, number), handles, cancellationToken).ConfigureAwait(false);
                RopMessageWrite.ParseDeleteAttachment(new RopReader(deleted));
            }

            if (photo is { Length: > 0 })
                await AddPhotoAttachmentAsync(session, handles, photo, cancellationToken).ConfigureAwait(false);

            var changes = new MapiPropertyEdits.Changes();
            changes.Set.Add(TaggedPropertyValue.Boolean(tags.HasPicture, photo is { Length: > 0 }));
            await MapiPropertyEdits.ApplyAsync(session, handles, 1, changes, "contact", cancellationToken).ConfigureAwait(false);
            await SaveAsync(session, handles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddPhotoAttachmentAsync(MapiSession session, uint[] handles, byte[] photo, CancellationToken cancellationToken)
    {
        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateAttachment(1, 3), handles, cancellationToken).ConfigureAwait(false);
        RopMessageWrite.ParseCreateAttachment(new RopReader(rops));
        var attachmentHandles = MapiSession.MergeHandles(handles, returned, PhotoSlots);

        try
        {
            var changes = new MapiPropertyEdits.Changes();
            changes.Set.AddRange(
            [
                TaggedPropertyValue.Long(PropertyTags.AttachMethod, 1),
                TaggedPropertyValue.Boolean(PropertyTags.AttachmentContactPhoto, true),
                TaggedPropertyValue.Unicode(AttachExtension, PhotoExtension),
                TaggedPropertyValue.Unicode(PropertyTags.AttachFilename, PhotoFileName),
                TaggedPropertyValue.Unicode(PropertyTags.AttachLongFilename, PhotoFileName),
                TaggedPropertyValue.Unicode(PropertyTags.DisplayName, PhotoFileName),
            ]);
            await MapiPropertyEdits.ApplyAsync(session, attachmentHandles, 3, changes, "contact photo", cancellationToken).ConfigureAwait(false);
            await MapiMessageComposer.WritePropertyAsync(session, attachmentHandles, 3, 4, PropertyTags.AttachDataBinary, photo, isUnicodeString: false, cancellationToken).ConfigureAwait(false);

            (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildSaveChangesAttachment(4, 3), attachmentHandles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseSaveChangesAttachment(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(attachmentHandles[3], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The attachment numbers of the message (open in slot 1) that are marked as the contact photo.</summary>
    private static async Task<List<uint>> FindPhotoAttachmentsAsync(MapiSession session, uint[] handles, CancellationToken cancellationToken)
    {
        uint[] columns = [PropertyTags.AttachNumber, PropertyTags.AttachmentContactPhoto];
        var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildGetAttachmentTable(1, 2), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseGetAttachmentTable(new RopReader(rops));
        var tableHandles = MapiSession.MergeHandles(handles, returned, PhotoSlots);

        try
        {
            (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, columns), tableHandles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseSetColumns(new RopReader(rops));

            var numbers = new List<uint>();
            while (true)
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 100), tableHandles, cancellationToken).ConfigureAwait(false);
                var rows = RopFolder.ParseQueryRows(new RopReader(rops), columns);
                if (rows.Count == 0)
                    break;

                numbers.AddRange(rows.Where(row => row[1].AsBoolean == true && row[0].AsUInt32 is not null).Select(row => row[0].AsUInt32 ?? 0));
            }

            return numbers;
        }
        finally
        {
            await session.ReleaseAsync(tableHandles[2], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadStreamAsync(MapiSession session, uint[] handles, byte objectHandleIndex, byte streamHandleIndex, uint tag, CancellationToken cancellationToken)
    {
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenStream(tag, objectHandleIndex, streamHandleIndex), handles, cancellationToken).ConfigureAwait(false);
        var size = RopMessage.ParseOpenStream(new RopReader(rops));
        var streamHandles = MapiSession.MergeHandles(handles, returned, PhotoSlots);

        try
        {
            var buffer = new MemoryStream((int)Math.Min(size, 64 * 1024 * 1024));
            while (buffer.Length < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var want = Math.Min(RopMessage.SafeReadChunk, size - (uint)buffer.Length);
                (rops, _) = await session.ExecuteAsync(RopMessage.BuildReadStream(streamHandleIndex, want), streamHandles, cancellationToken).ConfigureAwait(false);
                var data = RopMessage.ParseReadStream(new RopReader(rops));
                if (data.Length == 0)
                    break;
                buffer.Write(data);
            }

            return buffer.ToArray();
        }
        finally
        {
            await session.ReleaseAsync(streamHandles[streamHandleIndex], cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values) => values.Select(Clean).FirstOrDefault(v => v != null);

    private static string? Join(string separator, params string?[] parts)
    {
        var kept = parts.Select(Clean).Where(p => p != null).ToList();
        return kept.Count == 0 ? null : string.Join(separator, kept);
    }
}

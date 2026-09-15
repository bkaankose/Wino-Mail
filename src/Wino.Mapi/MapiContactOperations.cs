using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi;

/// <summary>The store's property tags for the contact named properties (PSETID_Address), resolved once per session.</summary>
public sealed record MapiContactTags(
    uint Email1, uint Email2, uint Email3,
    uint Email1DisplayName, uint Email1AddressType, uint Email1OriginalDisplayName,
    uint Email2AddressType, uint Email2OriginalDisplayName,
    uint Email3AddressType, uint Email3OriginalDisplayName,
    uint FileUnder,
    uint WorkStreet, uint WorkCity, uint WorkState, uint WorkPostalCode, uint WorkCountry);

/// <summary>One row of a Contacts folder, as this client reads it.</summary>
public sealed record MapiContactInfo(
    ulong MessageId,
    string MessageClass,
    string? DisplayName, string? GivenName, string? Surname,
    string? Company, string? JobTitle,
    string? BusinessPhone, string? HomePhone, string? MobilePhone, string? BusinessFax,
    string? Email1, string? Email2, string? Email3,
    string? WorkStreet, string? WorkCity, string? WorkState, string? WorkPostalCode, string? WorkCountry,
    string? Notes,
    bool HasAttachments)
{
    /// <summary>IPM.Contact and its subclasses; distribution lists (IPM.DistList) share the folder and are not contacts.</summary>
    public bool IsContact => MessageClass.StartsWith(PropertyTags.ContactMessageClass, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The fields this client writes to a contact (the inverse of the read, minus what the UI cannot edit).</summary>
public sealed class MapiContactWrite
{
    public string? DisplayName { get; set; }
    public string? Company { get; set; }
    public string? JobTitle { get; set; }
    public string? BusinessPhone { get; set; }
    public string? HomePhone { get; set; }
    public string? MobilePhone { get; set; }
    public string? BusinessFax { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Contacts over MAPI (rung 8): the Contacts folder found by its well-known entry id on the Inbox
/// (MS-OXOSFLD 2.2.3), rows read from its contents table with the fixed contact properties plus the
/// PSETID_Address named ones, and create / update as an IPM.Contact message (MS-OXOCNTC).
/// </summary>
public static class MapiContactOperations
{
    private const int Slots = 3;

    public static async Task<MapiContactTags> ResolveTagsAsync(MapiSession session, CancellationToken cancellationToken = default)
    {
        uint[] lids =
        [
            PropertyTags.LidEmail1EmailAddress, PropertyTags.LidEmail2EmailAddress, PropertyTags.LidEmail3EmailAddress,
            PropertyTags.LidEmail1DisplayName, PropertyTags.LidEmail1AddressType, PropertyTags.LidEmail1OriginalDisplayName,
            PropertyTags.LidEmail2AddressType, PropertyTags.LidEmail2OriginalDisplayName,
            PropertyTags.LidEmail3AddressType, PropertyTags.LidEmail3OriginalDisplayName,
            PropertyTags.LidFileUnder,
            PropertyTags.LidWorkAddressStreet, PropertyTags.LidWorkAddressCity, PropertyTags.LidWorkAddressState, PropertyTags.LidWorkAddressPostalCode, PropertyTags.LidWorkAddressCountry,
        ];

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 1);
        var names = lids.Select(lid => RopProperties.PropertyName.ById(PropertyTags.AddressPropertySet, lid)).ToList();
        var (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertyIdsFromNames(0, names), handles, cancellationToken).ConfigureAwait(false);
        var ids = RopProperties.ParseGetPropertyIdsFromNames(new RopReader(rops));
        if (ids.Count != lids.Length || ids.Any(id => id == 0))
            throw new MapiFormatException("The store did not map every contact named property.");

        uint Tag(int i) => ((uint)ids[i] << 16) | PropertyTypes.Unicode;
        return new MapiContactTags(Tag(0), Tag(1), Tag(2), Tag(3), Tag(4), Tag(5), Tag(6), Tag(7), Tag(8), Tag(9), Tag(10), Tag(11), Tag(12), Tag(13), Tag(14), Tag(15));
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

    private static uint[] Columns(MapiContactTags tags) =>
    [
        PropertyTags.Mid, PropertyTags.MessageClass,
        PropertyTags.DisplayName, PropertyTags.GivenName, PropertyTags.Surname,
        PropertyTags.CompanyName, PropertyTags.JobTitle,
        PropertyTags.BusinessTelephoneNumber, PropertyTags.HomeTelephoneNumber, PropertyTags.MobileTelephoneNumber, PropertyTags.BusinessFaxNumber,
        tags.Email1, tags.Email2, tags.Email3,
        tags.WorkStreet, tags.WorkCity, tags.WorkState, tags.WorkPostalCode, tags.WorkCountry,
        PropertyTags.Body, PropertyTags.HasAttachments,
        tags.Email1AddressType, tags.Email1OriginalDisplayName,
        tags.Email2AddressType, tags.Email2OriginalDisplayName,
        tags.Email3AddressType, tags.Email3OriginalDisplayName,
        tags.Email1DisplayName,
    ];

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
        if (open >= 0 && close > open && displayName!.AsSpan(open + 1, close - open - 1).Contains('@'))
            return displayName.Substring(open + 1, close - open - 1).Trim();

        return null;
    }

    public static async Task<List<MapiContactInfo>> ReadContactsAsync(MapiSession session, ulong folderId, MapiContactTags tags, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var columns = Columns(tags);
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
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Twenty string columns, one of them a body: small pages keep well inside the 32KB buffer.
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 20), handles, cancellationToken).ConfigureAwait(false);
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

                        var errors = row.Where(c => c.Error is not null).Select(c => $"{PropertyTags.Describe(c.Tag)}=0x{c.Error:X8}").ToList();
                        if (errors.Count > 0 && diagnostics is not null)
                            diagnostics($"contact 0x{mid:X16} cell errors: {string.Join(", ", errors)}");

                        contacts.Add(new MapiContactInfo(
                            mid,
                            row[1].AsString ?? string.Empty,
                            row[2].AsString, row[3].AsString, row[4].AsString,
                            row[5].AsString, row[6].AsString,
                            row[7].AsString, row[8].AsString, row[9].AsString, row[10].AsString,
                            ResolveSmtpAddress(row[11].AsString, row[21].AsString, row[22].AsString, row[27].AsString),
                            ResolveSmtpAddress(row[12].AsString, row[23].AsString, row[24].AsString),
                            ResolveSmtpAddress(row[13].AsString, row[25].AsString, row[26].AsString),
                            row[14].AsString, row[15].AsString, row[16].AsString, row[17].AsString, row[18].AsString,
                            row[19].AsString,
                            row[20].AsBoolean ?? false));
                    }
                }

                diagnostics?.Invoke($"contacts 0x{folderId:X16}: {contacts.Count} rows ({contacts.Count(c => c.IsContact)} contacts, RowCount {rowCount}, {contacts.Count(c => c.Email1 is not null)} with e-mail 1)");
                foreach (var c in contacts.Take(10))
                    diagnostics?.Invoke($"contact 0x{c.MessageId:X16} class={c.MessageClass} name={(c.DisplayName is null ? "-" : "set")} email1={(c.Email1 is null ? "-" : "set")} email2={(c.Email2 is null ? "-" : "set")} company={(c.Company is null ? "-" : "set")} phone={((c.BusinessPhone ?? c.MobilePhone) is null ? "-" : "set")} attachments={c.HasAttachments}");
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

    /// <summary>Creates an IPM.Contact in the folder; returns its message id.</summary>
    public static async Task<ulong> CreateContactAsync(MapiSession session, ulong folderId, MapiContactTags tags, MapiContactWrite contact, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateMessage(0, 1, folderId), handles, cancellationToken).ConfigureAwait(false);
        var createdId = RopMessageWrite.ParseCreateMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            var savedId = await SetAndSaveAsync(session, handles, tags, contact, includeClass: true, cancellationToken).ConfigureAwait(false);
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
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            await SetAndSaveAsync(session, handles, tags, contact, includeClass: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ulong> SetAndSaveAsync(MapiSession session, uint[] handles, MapiContactTags tags, MapiContactWrite contact, bool includeClass, CancellationToken cancellationToken)
    {
        var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(1, Properties(tags, contact, includeClass)), handles, cancellationToken).ConfigureAwait(false);
        var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
        if (problems.Count > 0)
            throw new MapiFormatException($"The store refused contact properties: {string.Join(", ", problems.Select(p => $"0x{p.Tag:X8}=0x{p.Error:X8}"))}");

        (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken).ConfigureAwait(false);
        return RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
    }

    /// <summary>The properties Outlook needs to show the contact as a contact (MS-OXOCNTC 2.2.1): class, names, "file as", e-mail 1 with its display name and type.</summary>
    public static List<TaggedPropertyValue> Properties(MapiContactTags tags, MapiContactWrite contact, bool includeClass)
    {
        var displayName = FirstNonEmpty(contact.DisplayName, contact.Email) ?? string.Empty;
        var values = new List<TaggedPropertyValue>();
        if (includeClass)
            values.Add(TaggedPropertyValue.Unicode(PropertyTags.MessageClass, PropertyTags.ContactMessageClass));

        values.Add(TaggedPropertyValue.Unicode(PropertyTags.DisplayName, displayName));
        values.Add(TaggedPropertyValue.Unicode(PropertyTags.Subject, displayName));
        values.Add(TaggedPropertyValue.Unicode(tags.FileUnder, displayName));
        values.Add(TaggedPropertyValue.Unicode(PropertyTags.CompanyName, contact.Company ?? string.Empty));
        values.Add(TaggedPropertyValue.Unicode(PropertyTags.JobTitle, contact.JobTitle ?? string.Empty));
        AddIfPresent(values, PropertyTags.BusinessTelephoneNumber, contact.BusinessPhone);
        AddIfPresent(values, PropertyTags.HomeTelephoneNumber, contact.HomePhone);
        AddIfPresent(values, PropertyTags.MobileTelephoneNumber, contact.MobilePhone);
        AddIfPresent(values, PropertyTags.BusinessFaxNumber, contact.BusinessFax);

        if (!string.IsNullOrWhiteSpace(contact.Email))
        {
            var email = contact.Email.Trim();
            values.Add(TaggedPropertyValue.Unicode(tags.Email1, email));
            values.Add(TaggedPropertyValue.Unicode(tags.Email1AddressType, "SMTP"));
            values.Add(TaggedPropertyValue.Unicode(tags.Email1OriginalDisplayName, email));
            values.Add(TaggedPropertyValue.Unicode(tags.Email1DisplayName, $"{displayName} ({email})"));
        }

        AddIfPresent(values, PropertyTags.Body, contact.Notes);
        return values;
    }

    /// <summary>The contact's photo (the ContactPicture.jpg attachment), or null.</summary>
    public static async Task<byte[]?> ReadContactPhotoAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken = default)
    {
        var content = await MapiMessageOperations.ReadMessageContentAsync(session, folderId, messageId, cancellationToken).ConfigureAwait(false);
        var photo = content.Attachments.FirstOrDefault(a => a.IsFile && a.Data is { Length: > 0 }
            && (string.Equals(a.FileName, "ContactPicture.jpg", StringComparison.OrdinalIgnoreCase) || a.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true));
        return photo?.Data;
    }

    private static void AddIfPresent(List<TaggedPropertyValue> values, uint tag, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(TaggedPropertyValue.Unicode(tag, value.Trim()));
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

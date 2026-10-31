using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi;

/// <summary>One row of a folder hierarchy table.</summary>
public sealed record MapiFolderInfo(
    ulong FolderId,
    ulong ParentFolderId,
    string DisplayName,
    string? ContainerClass,
    bool HasSubfolders,
    uint ContentCount,
    uint UnreadCount,
    bool IsHidden = false)
{
    /// <summary>MS-OXOSFLD: mail folders carry a container class of "IPF.Note" (or a subclass of it).</summary>
    public bool IsMailFolder => ContainerClass is { Length: > 0 } c && c.StartsWith("IPF.Note", StringComparison.OrdinalIgnoreCase);

    /// <summary>DatabaseGuid + GlobalCounter (22 bytes), set by <see cref="MapiFolderOperations.ResolveEntryIdsAsync"/>.</summary>
    public byte[]? LongTermId { get; set; }

    /// <summary>The 46-byte folder EntryID built from the long-term id, set alongside it.</summary>
    public byte[]? EntryId { get; set; }

    /// <summary>The entry id as uppercase hex, the form EWS ConvertId (HexEntryId) and MFCMAPI use.</summary>
    public string? EntryIdHex => EntryId is null ? null : Convert.ToHexString(EntryId);
}

/// <summary>Entry ids of the special folders that a logon does not name (MS-OXOSFLD 2.2).</summary>
public sealed record MapiSpecialFolderEntryIds(byte[]? Drafts, byte[]? Junk);

/// <summary>
/// Folder-level operations over a session: the hierarchy table and the special-folder entry ids.
/// The synchronizer turns the result into folder rows.
/// </summary>
public static class MapiFolderOperations
{
    /// <summary>
    /// Reads every folder under <paramref name="rootFolderId"/> (deep) with the hierarchy columns.
    /// The IPM subtree from the logon is the usual root: that is the tree a user sees.
    /// </summary>
    /// <param name="deep">Every descendant (the default) or only the direct children, which is all a lazily expanded public folder tree wants.</param>
    public static async Task<List<MapiFolderInfo>> ReadHierarchyAsync(MapiSession session, ulong rootFolderId, CancellationToken cancellationToken = default, Action<string>? diagnostics = null, bool deep = true)
    {
        // Handle table indices: 0 = logon, 1 = folder, 2 = table.
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 3);

        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(rootFolderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, 3);

        try
        {
            (rops, returned) = await session.ExecuteAsync(
                RopFolder.BuildGetHierarchyTable(1, 2, (deep ? RopFolder.HierarchyTableFlags.Depth : 0) | RopFolder.HierarchyTableFlags.DeferredErrors),
                handles, cancellationToken).ConfigureAwait(false);
            var rowCount = RopFolder.ParseGetHierarchyTable(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, 3);
            diagnostics?.Invoke($"hierarchy table under 0x{rootFolderId:X16}: RowCount {rowCount}");

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, PropertyTags.HierarchyColumnsWithHidden), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));

                var folders = new List<MapiFolderInfo>((int)Math.Min(rowCount, 4096));
                var page = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 100 rows of eight small columns stays well inside the 32KB response buffer.
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 100), handles, cancellationToken).ConfigureAwait(false);
                    var rows = RopFolder.ParseQueryRows(new RopReader(rops), PropertyTags.HierarchyColumnsWithHidden);
                    page++;

                    if (diagnostics is not null)
                    {
                        diagnostics($"hierarchy page {page}: {rows.Count} rows from {rops.Length} ROP bytes");
                        if (page == 1 && rows.Count > 0)
                        {
                            // Folder scaffolding only (ids, names, classes): fine for a local debug log.
                            diagnostics("first row: " + string.Join(", ", rows[0].Select((v, i) => $"{PropertyTags.Describe(PropertyTags.HierarchyColumnsWithHidden[i])}={DescribeCell(v)}")));
                            diagnostics("first page ROP head:" + Environment.NewLine + HexDump.Render(rops, 256));
                        }
                    }

                    if (rows.Count == 0)
                    {
                        break;
                    }

                    foreach (var row in rows)
                    {
                        var folder = ToFolderInfo(row);
                        if (folder is not null)
                        {
                            folders.Add(folder);
                        }
                    }
                }

                diagnostics?.Invoke($"hierarchy: {folders.Count} folders kept of the rows read");
                return folders;
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

    /// <summary>
    /// Gives every folder its long-term id and EntryID, via RopLongTermIdFromId batched on the logon
    /// handle. A folder the server would not convert keeps null ids; the caller reports it.
    /// </summary>
    public static async Task ResolveEntryIdsAsync(MapiSession session, IReadOnlyList<MapiFolderInfo> folders, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var mailboxGuid = session.Logon!.MailboxGuid;
        uint[] handles = [session.LogonHandle];

        // 50 responses of 32 bytes each is a small fraction of the 32KB response buffer.
        foreach (var batch in folders.Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = batch.Select(f => f.FolderId).ToList();
            var (rops, _) = await session.ExecuteAsync(RopIds.BuildLongTermIdFromIdBatch(0, ids), handles, cancellationToken).ConfigureAwait(false);

            List<byte[]> longTermIds;
            try
            {
                longTermIds = RopIds.ParseLongTermIdFromIdBatch(rops, batch.Length);
            }
            catch (MapiRopException ex)
            {
                // The batch stopped at a failing ROP; whatever parsed before it is still good.
                diagnostics?.Invoke($"long-term id batch stopped: {ex.Message}");
                longTermIds = [];
            }

            for (var i = 0; i < longTermIds.Count && i < batch.Length; i++)
            {
                batch[i].LongTermId = longTermIds[i];
                batch[i].EntryId = RopIds.BuildPrivateFolderEntryId(mailboxGuid, longTermIds[i]);
            }

            if (longTermIds.Count < batch.Length)
            {
                diagnostics?.Invoke($"long-term ids: {longTermIds.Count} of {batch.Length} resolved in this batch");
            }
        }
    }

    // --- Folder mutations (the last EWS handlers on the mail side) ---

    /// <summary>Creates a folder under <paramref name="parentFolderId"/>; returns its id.</summary>
    public static async Task<ulong> CreateFolderAsync(MapiSession session, ulong parentFolderId, string displayName, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 3);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(parentFolderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, 3);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopFolder.BuildCreateFolder(1, 2, displayName), handles, cancellationToken).ConfigureAwait(false);
            var folderId = RopFolder.ParseCreateFolder(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, 3);

            try
            {
                // A folder without a container class is not a mail folder to anyone (the hierarchy sync's
                // IsMailFolder filter would drop it, as would Outlook's mail view).
                (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(2, [TaggedPropertyValue.Unicode(PropertyTags.ContainerClass, "IPF.Note")]), handles, cancellationToken).ConfigureAwait(false);
                var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
                if (problems.Count > 0)
                    throw new MapiRopException("RopSetProperties(ContainerClass)", problems[0].Error);
            }
            finally
            {
                await session.ReleaseAsync(handles[2], cancellationToken).ConfigureAwait(false);
            }

            return folderId;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Renames a folder: PidTagDisplayName on the open folder; folders persist property writes immediately.</summary>
    public static async Task RenameFolderAsync(MapiSession session, ulong folderId, string displayName, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 2);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, 2);

        try
        {
            (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(1, [TaggedPropertyValue.Unicode(PropertyTags.DisplayName, displayName)]), handles, cancellationToken).ConfigureAwait(false);
            var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
            if (problems.Count > 0)
                throw new MapiRopException("RopSetProperties(DisplayName)", problems[0].Error);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes a folder and everything in it. Soft (to Deleted Items) is a move; this is the hard delete.</summary>
    public static async Task<bool> DeleteFolderAsync(MapiSession session, ulong parentFolderId, ulong folderId, bool hardDelete, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 2);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(parentFolderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, 2);

        try
        {
            var flags = RopFolder.DeleteFolderFlags.DeleteMessages | RopFolder.DeleteFolderFlags.DeleteFolders | (hardDelete ? RopFolder.DeleteFolderFlags.HardDelete : RopFolder.DeleteFolderFlags.None);
            (rops, _) = await session.ExecuteAsync(RopFolder.BuildDeleteFolder(1, folderId, flags), handles, cancellationToken).ConfigureAwait(false);
            return !RopFolder.ParseDeleteFolder(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Moves a folder under a new parent, keeping (or setting) its name.</summary>
    public static async Task<bool> MoveFolderAsync(MapiSession session, ulong folderId, ulong sourceParentFolderId, ulong destinationParentFolderId, string folderName, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 3);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(sourceParentFolderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, 3);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(destinationParentFolderId, 0, 2), handles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseOpenFolder(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, 3);

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildMoveFolder(1, 2, folderId, folderName), handles, cancellationToken).ConfigureAwait(false);
                return !RopFolder.ParseMoveFolder(new RopReader(rops));
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

    /// <summary>
    /// Reads the Drafts and Junk E-mail entry ids off the Inbox. The logon names Inbox, Outbox, Sent and
    /// Deleted by folder id; these two are only reachable this way (MS-OXOSFLD 2.2.3 / 2.2.4). A missing
    /// property means the mailbox has never had that folder provisioned; that is a null, not an error.
    /// </summary>
    public static async Task<MapiSpecialFolderEntryIds> ReadSpecialFolderEntryIdsAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken = default)
    {
        uint[] tags = [PropertyTags.IpmDraftsEntryId, PropertyTags.AdditionalRenEntryIds];

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 2);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(inboxFolderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, 2);

        try
        {
            (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(1, tags), handles, cancellationToken).ConfigureAwait(false);
            var values = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), tags);

            var drafts = values[0].AsBinary;
            var additional = values[1].Value as byte[][];
            var junk = additional is not null && additional.Length > PropertyTags.AdditionalRenEntryIdsJunkIndex
                ? additional[PropertyTags.AdditionalRenEntryIdsJunkIndex]
                : null;

            return new MapiSpecialFolderEntryIds(
                drafts is { Length: > 0 } ? drafts : null,
                junk is { Length: > 0 } ? junk : null);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    private static string DescribeCell(PropertyValue value) => value.Error is { } error
        ? $"error 0x{error:X8}"
        : value.Value switch
        {
            null => "absent",
            byte[] bytes => $"binary[{bytes.Length}]",
            string s => $"\"{s}\"",
            ulong u => $"0x{u:X16}",
            var other => $"{other} ({other.GetType().Name})",
        };

    /// <summary>
    /// Maps a hierarchy row. A row without a folder id is skipped: nothing can be keyed on it. A missing
    /// display name or class is tolerated (root-level system folders can lack a class).
    /// </summary>
    public static MapiFolderInfo? ToFolderInfo(PropertyValue[] row)
    {
        if (row[0].AsUInt64 is not { } folderId)
        {
            return null;
        }

        return new MapiFolderInfo(
            folderId,
            row[1].AsUInt64 ?? 0,
            row[2].AsString ?? string.Empty,
            row[3].AsString,
            row[4].AsBoolean ?? false,
            row[5].AsUInt32 ?? 0,
            row[6].AsUInt32 ?? 0,
            row.Length > 7 && (row[7].AsBoolean ?? false));
    }
}

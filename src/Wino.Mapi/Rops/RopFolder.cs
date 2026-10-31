using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// The folder and table ROPs: RopOpenFolder, RopGetContentsTable, RopSetColumns, RopQueryRows.
///
/// Each produces a server object handle that the next one consumes. Handles live in the session,
/// and each Execute carries a handle table mapping the indices its ROPs reference.
/// </summary>
public static class RopFolder
{
    public const byte RopOpenFolder = 0x02;
    public const byte RopGetHierarchyTable = 0x04;
    public const byte RopGetContentsTable = 0x05;
    public const byte RopCreateFolder = 0x1C;
    public const byte RopDeleteFolder = 0x1D;
    public const byte RopMoveFolder = 0x35;
    public const byte RopGetRulesTable = 0x3F;

    /// <summary>RopGetRulesTable (MS-OXCROPS 2.2.11.2) on an open folder; the table's rows are the folder's rules.</summary>
    public static byte[] BuildGetRulesTable(byte folderHandleIndex, byte outputHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopGetRulesTable);
        rop.UInt8(0);
        rop.UInt8(folderHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt8(0x40);                 // TableFlags: Unicode
        return rop.ToArray();
    }

    public static void ParseGetRulesTable(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopGetRulesTable, nameof(RopGetRulesTable));

    public const byte RopModifyRules = 0x41;

    [Flags]
    public enum RuleDataFlags : byte
    {
        Add = 0x01,
        Modify = 0x02,
        Remove = 0x04,
    }

    /// <summary>
    /// One RuleData (MS-OXORULE 2.2.1.3.1): Add carries a whole rule (no PidTagRuleId); Modify and
    /// Remove name the rule by PidTagRuleId, Modify with the properties that change.
    /// </summary>
    public sealed record RuleData(RuleDataFlags Flags, IReadOnlyList<TaggedPropertyValue> Values);

    /// <summary>RopModifyRules (MS-OXCROPS 2.2.11.1) on an open folder.</summary>
    public static byte[] BuildModifyRules(byte folderHandleIndex, IReadOnlyList<RuleData> rules, bool replaceAll = false)
    {
        var rop = new RopWriter();
        rop.UInt8(RopModifyRules);
        rop.UInt8(0);
        rop.UInt8(folderHandleIndex);
        rop.UInt8(replaceAll ? (byte)0x01 : (byte)0x00);    // ModifyRulesFlags: ReplaceAll
        rop.UInt16((ushort)rules.Count);
        foreach (var rule in rules)
        {
            rop.UInt8((byte)rule.Flags);
            rop.UInt16((ushort)rule.Values.Count);
            foreach (var value in rule.Values)
            {
                value.WriteTo(rop);
            }
        }

        return rop.ToArray();
    }

    public static void ParseModifyRules(RopReader reader)
        => RopExecute.ExpectSuccess(reader, RopModifyRules, nameof(RopModifyRules));

    /// <summary>RopCreateFolder (MS-OXCROPS 2.2.4.2) under the folder in <paramref name="parentHandleIndex"/>. Folders save at once; there is no SaveChanges for them.</summary>
    public static byte[] BuildCreateFolder(byte parentHandleIndex, byte outputHandleIndex, string displayName, string comment = "")
    {
        var rop = new RopWriter();
        rop.UInt8(RopCreateFolder);
        rop.UInt8(0);
        rop.UInt8(parentHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt8(0x01);                 // FolderType: generic
        rop.UInt8(1);                    // UseUnicodeStrings
        rop.UInt8(0);                    // OpenExisting: fail if a folder of that name exists
        rop.UInt8(0);                    // Reserved
        rop.UnicodeZ(displayName);
        rop.UnicodeZ(comment);
        return rop.ToArray();
    }

    /// <summary>Returns the new folder's id (or the existing one's when OpenExisting matched).</summary>
    public static ulong ParseCreateFolder(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopCreateFolder, nameof(RopCreateFolder));
        var folderId = reader.UInt64();
        reader.UInt8();                  // IsExistingFolder (the fields after it only matter when it is set)
        return folderId;
    }

    [Flags]
    public enum DeleteFolderFlags : byte
    {
        None = 0x00,
        DeleteMessages = 0x01,
        DeleteFolders = 0x04,
        HardDelete = 0x10,
    }

    /// <summary>RopDeleteFolder (MS-OXCROPS 2.2.4.3): on the PARENT folder's handle, naming the child.</summary>
    public static byte[] BuildDeleteFolder(byte parentHandleIndex, ulong folderId, DeleteFolderFlags flags)
    {
        var rop = new RopWriter();
        rop.UInt8(RopDeleteFolder);
        rop.UInt8(0);
        rop.UInt8(parentHandleIndex);
        rop.UInt8((byte)flags);
        rop.UInt64(folderId);
        return rop.ToArray();
    }

    /// <summary>Returns PartialCompletion.</summary>
    public static bool ParseDeleteFolder(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopDeleteFolder, nameof(RopDeleteFolder));
        return reader.UInt8() != 0;
    }

    /// <summary>RopMoveFolder (MS-OXCROPS 2.2.4.7): source and destination PARENT handles, the folder to move, its name at the destination.</summary>
    public static byte[] BuildMoveFolder(byte sourceParentHandleIndex, byte destinationParentHandleIndex, ulong folderId, string newFolderName)
    {
        var rop = new RopWriter();
        rop.UInt8(RopMoveFolder);
        rop.UInt8(0);
        rop.UInt8(sourceParentHandleIndex);
        rop.UInt8(destinationParentHandleIndex);
        rop.UInt8(0);                    // WantAsynchronous
        rop.UInt8(1);                    // UseUnicode
        rop.UInt64(folderId);
        rop.UnicodeZ(newFolderName);
        return rop.ToArray();
    }

    public static bool ParseMoveFolder(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopMoveFolder, nameof(RopMoveFolder));
        return reader.UInt8() != 0;      // PartialCompletion
    }

    public const byte RopSetColumns = 0x12;
    public const byte RopSortTable = 0x13;
    public const byte RopQueryRows = 0x15;

    /// <summary>One sort key for RopSortTable (MS-OXCDATA 2.13.1).</summary>
    public readonly record struct SortOrder(uint PropertyTag, bool Descending);

    /// <summary>RopSortTable (MS-OXCROPS 2.2.5.2): orders a contents table; synchronous.</summary>
    public static byte[] BuildSortTable(byte tableHandleIndex, IReadOnlyList<SortOrder> sortOrders)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSortTable);
        rop.UInt8(0);
        rop.UInt8(tableHandleIndex);
        rop.UInt8(0);                    // SortTableFlags: synchronous
        rop.UInt16((ushort)sortOrders.Count);
        rop.UInt16(0);                   // CategoryCount
        rop.UInt16(0);                   // ExpandedCount
        foreach (var order in sortOrders)
        {
            rop.UInt16((ushort)(order.PropertyTag & 0xFFFF));   // PropertyType
            rop.UInt16((ushort)(order.PropertyTag >> 16));      // PropertyId
            rop.UInt8(order.Descending ? (byte)1 : (byte)0);   // Order: 0 ascending, 1 descending
        }

        return rop.ToArray();
    }

    /// <summary>Returns TableStatus.</summary>
    public static byte ParseSortTable(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopSortTable, nameof(RopSortTable));
        return reader.UInt8();
    }

    /// <summary>
    /// TableFlags for RopGetContentsTable (MS-OXCFOLD 2.2.1.14). Associated selects the FAI table,
    /// the hidden sibling of the message table, where rules and the junk rule live.
    /// </summary>
    [Flags]
    public enum TableFlags : byte
    {
        None = 0x00,
        DeferredErrors = 0x01,
        Associated = 0x02,
        NoNotifications = 0x04,
        SoftDeletes = 0x08,
        UseUnicode = 0x10,
    }

    public static byte[] BuildOpenFolder(ulong folderId, byte inputHandleIndex, byte outputHandleIndex)
    {
        var rop = new RopWriter();
        rop.UInt8(RopOpenFolder);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(inputHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt64(folderId);
        rop.UInt8(0);                    // OpenModeFlags: OpenSoftDeleted = 0x04; 0 is fine
        return rop.ToArray();
    }

    /// <summary>
    /// TableFlags for RopGetHierarchyTable (MS-OXCFOLD 2.2.1.13.1). Depth asks for every descendant,
    /// not only immediate children, which is what a whole-mailbox folder sync wants in one table.
    /// </summary>
    [Flags]
    public enum HierarchyTableFlags : byte
    {
        None = 0x00,
        Depth = 0x04,
        DeferredErrors = 0x08,
        NoNotifications = 0x10,
        SoftDeletes = 0x20,
        UseUnicode = 0x40,
        SuppressNotifications = 0x80,
    }

    public static byte[] BuildGetHierarchyTable(byte inputHandleIndex, byte outputHandleIndex, HierarchyTableFlags flags)
    {
        var rop = new RopWriter();
        rop.UInt8(RopGetHierarchyTable);
        rop.UInt8(0);
        rop.UInt8(inputHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt8((byte)flags);
        return rop.ToArray();
    }

    /// <summary>Returns the table's row count.</summary>
    public static uint ParseGetHierarchyTable(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopGetHierarchyTable, nameof(RopGetHierarchyTable));
        return reader.UInt32();
    }

    public static byte[] BuildGetContentsTable(byte inputHandleIndex, byte outputHandleIndex, TableFlags flags)
    {
        var rop = new RopWriter();
        rop.UInt8(RopGetContentsTable);
        rop.UInt8(0);
        rop.UInt8(inputHandleIndex);
        rop.UInt8(outputHandleIndex);
        rop.UInt8((byte)flags);
        return rop.ToArray();
    }

    public static byte[] BuildSetColumns(byte inputHandleIndex, IReadOnlyList<uint> propertyTags)
    {
        var rop = new RopWriter();
        rop.UInt8(RopSetColumns);
        rop.UInt8(0);
        rop.UInt8(inputHandleIndex);
        rop.UInt8(0);                    // SetColumnsFlags: synchronous
        rop.UInt16((ushort)propertyTags.Count);
        foreach (var tag in propertyTags)
        {
            rop.UInt32(tag);
        }

        return rop.ToArray();
    }

    public static byte[] BuildQueryRows(byte inputHandleIndex, ushort rowCount)
    {
        var rop = new RopWriter();
        rop.UInt8(RopQueryRows);
        rop.UInt8(0);
        rop.UInt8(inputHandleIndex);
        rop.UInt8(0);                    // QueryRowsFlags: Advance
        rop.UInt8(1);                    // ForwardRead
        rop.UInt16(rowCount);
        return rop.ToArray();
    }

    /// <summary>
    /// RopOpenFolder response (MS-OXCROPS 2.2.4.1.2): HasRules, IsGhosted and, for a ghosted folder,
    /// the replica list (ServerCount, CheapServerCount, Servers). Public folders in a multi-mailbox
    /// deployment are routinely ghosted; the handle still works for the hierarchy and, when this
    /// mailbox holds the content, for the contents table. Returns the replica servers, empty otherwise.
    /// </summary>
    public static List<string> ParseOpenFolder(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopOpenFolder, nameof(RopOpenFolder));
        reader.UInt8();                  // HasRules
        var isGhosted = reader.UInt8();
        var servers = new List<string>();
        if (isGhosted != 0)
        {
            var serverCount = reader.UInt16();
            reader.UInt16();             // CheapServerCount
            for (var i = 0; i < serverCount && reader.Remaining > 0; i++)
                servers.Add(reader.AsciiZ());
        }
        return servers;
    }

    /// <summary>Returns the table's row count.</summary>
    public static uint ParseGetContentsTable(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopGetContentsTable, nameof(RopGetContentsTable));
        return reader.UInt32();
    }

    /// <summary>Returns TableStatus.</summary>
    public static byte ParseSetColumns(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopSetColumns, nameof(RopSetColumns));
        return reader.UInt8();
    }

    /// <summary>
    /// RopQueryRows response: Origin, RowCount, then that many PropertyRow structures in the column
    /// order most recently set by RopSetColumns.
    /// </summary>
    public static List<PropertyValue[]> ParseQueryRows(RopReader reader, IReadOnlyList<uint> columns)
    {
        RopExecute.ExpectSuccess(reader, RopQueryRows, nameof(RopQueryRows));

        reader.UInt8();                                    // Origin
        var rowCount = reader.UInt16();

        var rows = new List<PropertyValue[]>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            rows.Add(PropertyRow.Read(reader, columns));
        }

        return rows;
    }
}

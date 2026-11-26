using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Providers.Exchange.MAPI.Rules;
using Wino.Providers.Exchange.MAPI.Wire;

namespace Wino.Providers.Exchange.MAPI;

/// <summary>The category list configuration message of a Calendar folder (MS-OXOCFG 2.2.5.2.2).</summary>
public sealed record MapiCategoryListMessage(ulong MessageId, uint RoamingDatatypes);

/// <summary>
/// Mail categories over MAPI: a message's categories are PidNameKeywords (PS_PUBLIC_STRINGS "Keywords",
/// PtypMultipleString, MS-OXPROPS 2.452), and the master list is the XML stream (PidTagRoamingXmlStream) of the
/// IPM.Configuration.CategoryList FAI message in the Calendar folder (MS-OXOCFG 2.2.5.2.2, 3.1.4.1, 3.1.4.2).
/// </summary>
public static class MapiCategoryOperations
{
    public const string CategoryListMessageClass = "IPM.Configuration.CategoryList";

    /// <summary>PidTagRoamingDatatypes flag: the message carries an XML stream (MS-OXOCFG 2.2.2.1).</summary>
    public const uint RoamingDatatypeXmlStream = 0x00000002;

    public const byte RopDeleteProperties = 0x0B;

    private const int Slots = 5;

    private static readonly uint[] ConfigurationColumns =
        [PropertyTags.MessageClass, PropertyTags.Mid, PropertyTags.RoamingDatatypes, PropertyTags.LastModificationTime];

    /// <summary>
    /// The category list message, or null when the mailbox has none. When several exist (MS-OXOCFG 3.1.4.1), the most
    /// recently modified one that has an XML stream wins, else the most recently modified one.
    /// </summary>
    public static async Task<MapiCategoryListMessage?> FindCategoryListAsync(MapiSession session, ulong calendarFolderId, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(calendarFolderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopFolder.BuildGetContentsTable(1, 2, RopFolder.TableFlags.Associated), handles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseGetContentsTable(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, ConfigurationColumns), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));

                var candidates = new List<(ulong MessageId, uint Datatypes, DateTime Modified)>();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 100), handles, cancellationToken).ConfigureAwait(false);
                    var rows = RopFolder.ParseQueryRows(new RopReader(rops), ConfigurationColumns);
                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        if (string.Equals(row[0].AsString, CategoryListMessageClass, StringComparison.OrdinalIgnoreCase) && row[1].AsUInt64 is { } mid)
                            candidates.Add((mid, row[2].AsUInt32 ?? 0, row[3].AsDateTime ?? DateTime.MinValue));
                    }
                }

                var chosen = candidates
                    .OrderByDescending(c => (c.Datatypes & RoamingDatatypeXmlStream) != 0)
                    .ThenByDescending(c => c.Modified)
                    .Cast<(ulong MessageId, uint Datatypes, DateTime Modified)?>()
                    .FirstOrDefault();

                return chosen is { } found ? new MapiCategoryListMessage(found.MessageId, found.Datatypes) : null;
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

    /// <summary>The XML stream of the category list message; null when the message carries none.</summary>
    public static async Task<byte[]?> ReadCategoryListXmlAsync(MapiSession session, ulong calendarFolderId, MapiCategoryListMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await MapiRulesOperations.ReadPropertyStreamAsync(session, calendarFolderId, message.MessageId, PropertyTags.RoamingXmlStream, cancellationToken).ConfigureAwait(false);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the XML stream onto the category list message, creating the FAI message when <paramref name="existing"/>
    /// is null (MS-OXOCFG 3.1.4.2: RopCreateMessage with the Associated flag).
    /// </summary>
    public static async Task WriteCategoryListXmlAsync(MapiSession session, ulong calendarFolderId, MapiCategoryListMessage? existing, byte[] xml, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        if (existing is null)
        {
            var (created, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateMessage(0, 1, calendarFolderId, associated: true), handles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseCreateMessage(new RopReader(created));
            handles = MapiSession.MergeHandles(handles, returned, Slots);
        }
        else
        {
            var (opened, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(calendarFolderId, existing.MessageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
            RopMessage.ParseOpenMessage(new RopReader(opened));
            handles = MapiSession.MergeHandles(handles, returned, Slots);
        }

        try
        {
            var datatypes = (existing?.RoamingDatatypes ?? 0) | RoamingDatatypeXmlStream;
            List<TaggedPropertyValue> values = existing is null
                ? [TaggedPropertyValue.Unicode(PropertyTags.MessageClass, CategoryListMessageClass), TaggedPropertyValue.Long(PropertyTags.RoamingDatatypes, datatypes)]
                : [TaggedPropertyValue.Long(PropertyTags.RoamingDatatypes, datatypes)];

            var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(1, values), handles, cancellationToken).ConfigureAwait(false);
            var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
            if (problems.Count > 0)
                throw new MapiRopException($"RopSetProperties({PropertyTags.Describe(problems[0].Tag)})", problems[0].Error);

            await MapiMessageComposer.WritePropertyAsync(session, handles, 1, 2, PropertyTags.RoamingXmlStream, xml, isUnicodeString: false, cancellationToken).ConfigureAwait(false);

            (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken, interactive: true).ConfigureAwait(false);
            RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A message's categories from a row that asked for PidNameKeywords: null when the row did not ask for it, empty
    /// when the message has none.
    /// </summary>
    public static IReadOnlyList<string>? ReadKeywords(MapiMessageInfo message, uint keywordsTag)
    {
        var index = Array.FindIndex(message.Cells, cell => cell.Tag == keywordsTag);
        if (index < 0)
            return null;

        return message.Cells[index].Value switch
        {
            IEnumerable<object> values => [.. values.OfType<string>()],
            _ => []
        };
    }

    /// <summary>
    /// Replaces a message's categories. An empty list removes PidNameKeywords (RopDeleteProperties, MS-OXCROPS
    /// 2.2.8.8) rather than writing an empty multi-valued property.
    /// </summary>
    public static async Task SetKeywordsAsync(MapiSession session, ulong folderId, ulong messageId, uint keywordsTag, IReadOnlyList<string> keywords, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            List<(uint Tag, uint Error)> problems;
            if (keywords.Count > 0)
            {
                (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(1, [new TaggedPropertyValue(keywordsTag, keywords.ToArray())]), handles, cancellationToken).ConfigureAwait(false);
                problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
            }
            else
            {
                (rops, _) = await session.ExecuteAsync(BuildDeleteProperties(1, [keywordsTag]), handles, cancellationToken).ConfigureAwait(false);
                problems = ParseDeleteProperties(new RopReader(rops));
            }

            // A delete of a property the message does not have is reported as a problem, and it already has the state asked for.
            problems.RemoveAll(problem => keywords.Count == 0 && problem.Error == MapiRopException.NotFound);
            if (problems.Count > 0)
                throw new MapiRopException("RopSetProperties(Keywords)", problems[0].Error);

            (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken, interactive: true).ConfigureAwait(false);
            RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>RopDeleteProperties (MS-OXCROPS 2.2.8.8) on an open message.</summary>
    private static byte[] BuildDeleteProperties(byte handleIndex, IReadOnlyList<uint> tags)
    {
        var rop = new RopWriter();
        rop.UInt8(RopDeleteProperties);
        rop.UInt8(0);
        rop.UInt8(handleIndex);
        rop.UInt16((ushort)tags.Count);
        foreach (var tag in tags)
            rop.UInt32(tag);

        return rop.ToArray();
    }

    private static List<(uint Tag, uint Error)> ParseDeleteProperties(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopDeleteProperties, nameof(RopDeleteProperties));
        var problemCount = reader.UInt16();
        var problems = new List<(uint, uint)>(problemCount);
        for (var i = 0; i < problemCount; i++)
        {
            reader.UInt16();
            problems.Add((reader.UInt32(), reader.UInt32()));
        }

        return problems;
    }
}

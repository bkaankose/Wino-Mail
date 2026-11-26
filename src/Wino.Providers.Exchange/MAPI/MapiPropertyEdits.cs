using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Providers.Exchange.MAPI.Wire;

namespace Wino.Providers.Exchange.MAPI;

/// <summary>
/// Property edits on an open message or attachment for the contact and task writes: the property values to set, the
/// properties to remove, and a set that can carry PtypMultipleInteger32 values (MS-OXCDATA 2.11.1), which the shared
/// <see cref="TaggedPropertyValue"/> encoder does not.
/// </summary>
public static class MapiPropertyEdits
{
    public const byte RopDeleteProperties = 0x0B;
    public const ushort MultipleLong = 0x1003;

    /// <summary>A set of values to write and of property tags to remove, applied before the object is saved.</summary>
    public sealed class Changes
    {
        public List<TaggedPropertyValue> Set { get; } = [];
        public List<(uint Tag, uint[] Values)> SetMultipleLong { get; } = [];
        public List<uint> Delete { get; } = [];

        public void Text(uint tag, string? value, bool deleteWhenEmpty)
        {
            if (!string.IsNullOrWhiteSpace(value))
                Set.Add(TaggedPropertyValue.Unicode(tag, value.Trim()));
            else if (deleteWhenEmpty)
                Delete.Add(tag);
        }
    }

    /// <summary>RopSetProperties (MS-OXCROPS 2.2.8.6) whose values may include PtypMultipleInteger32 ones.</summary>
    public static byte[] BuildSetProperties(byte handleIndex, IReadOnlyList<TaggedPropertyValue> values, IReadOnlyList<(uint Tag, uint[] Values)> multipleLongValues)
    {
        var body = new RopWriter();
        body.UInt16((ushort)(values.Count + multipleLongValues.Count));
        foreach (var value in values)
            value.WriteTo(body);

        foreach (var (tag, items) in multipleLongValues)
        {
            if ((tag & 0xFFFF) != MultipleLong)
                throw new MapiFormatException($"Property 0x{tag:X8} is not a PtypMultipleInteger32 property.");

            body.UInt32(tag);
            body.UInt32((uint)items.Length);
            foreach (var item in items)
                body.UInt32(item);
        }

        var payload = body.ToArray();
        var rop = new RopWriter();
        rop.UInt8(RopMessageOps.RopSetProperties);
        rop.UInt8(0);
        rop.UInt8(handleIndex);
        rop.UInt16((ushort)payload.Length);
        rop.Bytes(payload);
        return rop.ToArray();
    }

    /// <summary>RopDeleteProperties (MS-OXCROPS 2.2.8.8): RopId, LogonId, InputHandleIndex, PropertyTagCount, PropertyTags.</summary>
    public static byte[] BuildDeleteProperties(byte handleIndex, IReadOnlyList<uint> tags)
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

    /// <summary>The success response (MS-OXCROPS 2.2.8.8.2): PropertyProblemCount and the PropertyProblem structures.</summary>
    public static List<(uint Tag, uint Error)> ParseDeleteProperties(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopDeleteProperties, nameof(RopDeleteProperties));
        var count = reader.UInt16();
        var problems = new List<(uint, uint)>(count);
        for (var i = 0; i < count; i++)
        {
            reader.UInt16();
            problems.Add((reader.UInt32(), reader.UInt32()));
        }

        return problems;
    }

    /// <summary>
    /// Applies the changes to the object in <paramref name="handleIndex"/>. A removal of a property the object does
    /// not have is reported as not found and is not an error; any other refused property fails the write.
    /// </summary>
    public static async Task ApplyAsync(MapiSession session, uint[] handles, byte handleIndex, Changes changes, string what, CancellationToken cancellationToken)
    {
        if (changes.Set.Count > 0 || changes.SetMultipleLong.Count > 0)
        {
            var (rops, _) = await session.ExecuteAsync(BuildSetProperties(handleIndex, changes.Set, changes.SetMultipleLong), handles, cancellationToken).ConfigureAwait(false);
            var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
            if (problems.Count > 0)
                throw new MapiFormatException($"The store refused {what} properties: {string.Join(", ", problems.Select(p => $"0x{p.Tag:X8}=0x{p.Error:X8}"))}");
        }

        var deletions = changes.Delete.Distinct().ToList();
        if (deletions.Count > 0)
        {
            var (rops, _) = await session.ExecuteAsync(BuildDeleteProperties(handleIndex, deletions), handles, cancellationToken).ConfigureAwait(false);
            var problems = ParseDeleteProperties(new RopReader(rops));
            var refused = problems.Where(p => p.Error != MapiRopException.NotFound).ToList();
            if (refused.Count > 0)
                throw new MapiFormatException($"The store refused to remove {what} properties: {string.Join(", ", refused.Select(p => $"0x{p.Tag:X8}=0x{p.Error:X8}"))}");
        }
    }
}

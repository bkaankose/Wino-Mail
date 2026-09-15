using System.Text;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// RopGetPropertiesSpecific (MS-OXCROPS 2.2.8.3): read named properties of an open object inline.
/// Fine for small values; a property past roughly 8KB comes back as PtypErrorCode NotEnoughMemory and
/// has to be streamed instead (<see cref="RopMessage"/>).
/// </summary>
public static class RopProperties
{
    public const byte RopGetPropertiesSpecific = 0x07;

    public static byte[] BuildGetPropertiesSpecific(byte inputHandleIndex, IReadOnlyList<uint> propertyTags, ushort propertySizeLimit = 0)
    {
        var rop = new RopWriter();
        rop.UInt8(RopGetPropertiesSpecific);
        rop.UInt8(0);                    // LogonId
        rop.UInt8(inputHandleIndex);
        rop.UInt16(propertySizeLimit);   // 0: no limit beyond the buffer's
        rop.UInt16(1);                   // WantUnicode
        rop.UInt16((ushort)propertyTags.Count);
        foreach (var tag in propertyTags)
        {
            rop.UInt32(tag);
        }

        return rop.ToArray();
    }

    /// <summary>The response carries one PropertyRow in the order the tags were requested.</summary>
    public static PropertyValue[] ParseGetPropertiesSpecific(RopReader reader, IReadOnlyList<uint> propertyTags)
    {
        RopExecute.ExpectSuccess(reader, RopGetPropertiesSpecific, nameof(RopGetPropertiesSpecific));
        return PropertyRow.Read(reader, propertyTags);
    }

    public const byte RopGetPropertyIdsFromNames = 0x56;

    /// <summary>A PropertyName (MS-OXCDATA 2.6.1): a property set plus either a LID (Kind 0x00) or a string name (Kind 0x01).</summary>
    public sealed record PropertyName(Guid PropertySet, uint? Lid, string? Name)
    {
        public static PropertyName ById(Guid propertySet, uint lid) => new(propertySet, lid, null);
        public static PropertyName ByName(Guid propertySet, string name) => new(propertySet, null, name);
    }

    public static byte[] BuildGetPropertyIdsFromNames(byte inputHandleIndex, IReadOnlyList<(Guid PropertySet, string Name)> names)
        => BuildGetPropertyIdsFromNames(inputHandleIndex, names.Select(n => PropertyName.ByName(n.PropertySet, n.Name)).ToList());

    /// <summary>
    /// RopGetPropertyIdsFromNames (MS-OXCROPS 2.2.8.1) on the logon: maps named properties to this
    /// store's property ids, creating them when absent.
    /// </summary>
    public static byte[] BuildGetPropertyIdsFromNames(byte inputHandleIndex, IReadOnlyList<PropertyName> names)
    {
        var rop = new RopWriter();
        rop.UInt8(RopGetPropertyIdsFromNames);
        rop.UInt8(0);
        rop.UInt8(inputHandleIndex);
        rop.UInt8(0x02);                 // Flags: Create
        rop.UInt16((ushort)names.Count);
        foreach (var name in names)
        {
            if (name.Lid is { } lid)
            {
                rop.UInt8(0x00);         // Kind: named by LID
                rop.Bytes(name.PropertySet.ToByteArray());
                rop.UInt32(lid);
            }
            else
            {
                // Kind 0x01 (MS-OXCDATA 2.6.1): NameSize is one byte counting the UTF-16 name AND its two
                // terminating zero bytes. Both a bare NUL-terminated name and a size-prefixed name without
                // the terminator were refused with ecRpcFormat (2026-09-04).
                var bytes = Encoding.Unicode.GetBytes(name.Name ?? throw new MapiFormatException("A string-named property needs a name."));
                rop.UInt8(0x01);
                rop.Bytes(name.PropertySet.ToByteArray());
                rop.UInt8((byte)(bytes.Length + 2));
                rop.Bytes(bytes);
                rop.UInt16(0);
            }
        }

        return rop.ToArray();
    }

    /// <summary>Returns the property ids in request order (0 when the server could not map one).</summary>
    public static List<ushort> ParseGetPropertyIdsFromNames(RopReader reader)
    {
        RopExecute.ExpectSuccess(reader, RopGetPropertyIdsFromNames, nameof(RopGetPropertyIdsFromNames));
        var count = reader.UInt16();
        var ids = new List<ushort>(count);
        for (var i = 0; i < count; i++)
        {
            ids.Add(reader.UInt16());
        }

        return ids;
    }
}

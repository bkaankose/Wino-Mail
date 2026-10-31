using System.Text;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Restrictions;

/// <summary>
/// Codec for serialized restrictions (MS-OXCDATA 2.12): a rule's condition in a rules-table row
/// (PtypRestriction) and in PidTagExtendedRuleMessageCondition, and the filter in RopSyncConfigure.
///
/// Only the restriction types rules actually use are implemented. Anything else throws by name rather
/// than being skipped, so an unhandled case shows up as a clear failure instead of a silently
/// truncated rule.
/// </summary>
public static class Restriction
{
    public const byte TypeAnd = 0x00;
    public const byte TypeOr = 0x01;
    public const byte TypeNot = 0x02;
    public const byte TypeContent = 0x03;
    public const byte TypeProperty = 0x04;
    public const byte TypeBitMask = 0x06;
    public const byte TypeSize = 0x07;
    public const byte TypeExist = 0x08;
    public const byte TypeSubObject = 0x09;
    public const byte TypeComment = 0x0A;
    public const byte TypeCount = 0x0B;

    /// <summary>
    /// Decodes PidTagExtendedRuleMessageCondition, which is a NamedPropertyInformation block
    /// (MS-OXORULE 2.2.4.1.10) followed by the restriction proper.
    /// </summary>
    public static RestrictionNode DecodeExtendedRuleCondition(ReadOnlyMemory<byte> condition)
    {
        var reader = new RopReader(condition);

        var namedPropertyCount = reader.UInt16();
        if (namedPropertyCount > 0)
        {
            // PropIds, then a sized blob of PropertyName structures. The rule may reference named
            // properties (categories, custom flags); the definitions are skipped but the count is
            // recorded so callers know the condition is not self-describing.
            for (var i = 0; i < namedPropertyCount; i++)
            {
                reader.UInt16();
            }

            var namedPropertiesSize = reader.UInt32();
            reader.Bytes((int)namedPropertiesSize);
        }

        // Extended rules use the wide form of the restriction encoding; see Decode.
        var node = Decode(reader, extendedFormat: true);
        node.NamedPropertyCount = namedPropertyCount;
        return node;
    }

    /// <summary>
    /// Encodes PidTagExtendedRuleMessageCondition: a NamedPropertyInformation block with no entries,
    /// then the restriction in its wide form. A tree decoded from a condition that carried named
    /// properties cannot be written back faithfully (the definitions were skipped), so it is refused.
    /// </summary>
    public static byte[] EncodeExtendedRuleCondition(RestrictionNode node)
    {
        if (node.NamedPropertyCount > 0)
            throw new MapiFormatException("The rule condition references named properties this client did not keep; it is not rewritten.");

        var writer = new RopWriter();
        writer.UInt16(0);                                    // NoOfNamedProps
        Encode(writer, node, extendedFormat: true);
        return writer.ToArray();
    }

    /// <summary>
    /// <paramref name="extendedFormat"/> selects the count width of AND/OR restrictions.
    ///
    /// This is the one place the two flavours of the encoding differ and it is easy to miss: a
    /// restriction inside a ROP request counts its subrestrictions in 2 bytes, but the same
    /// structure serialized into an extended rule counts them in 4. Read the narrow form against
    /// the wide one and you consume half the count, then treat the high half as the next opcode,
    /// which surfaces as a bogus "unhandled restriction type" a few bytes later rather than as an
    /// obviously wrong count.
    /// </summary>
    public static RestrictionNode Decode(RopReader reader, bool extendedFormat = false)
    {
        var type = reader.UInt8();

        switch (type)
        {
            case TypeAnd:
            case TypeOr:
            {
                var count = extendedFormat ? reader.UInt32() : reader.UInt16();
                var children = new List<RestrictionNode>((int)count);
                for (var i = 0; i < count; i++)
                {
                    children.Add(Decode(reader, extendedFormat));
                }

                return new RestrictionNode(type == TypeAnd ? RestrictionKind.And : RestrictionKind.Or) { Children = children };
            }

            case TypeNot:
                return new RestrictionNode(RestrictionKind.Not) { Children = [Decode(reader, extendedFormat)] };

            case TypeContent:
            {
                var fuzzyLow = reader.UInt16();
                var fuzzyHigh = reader.UInt16();
                var propertyTag = reader.UInt32();
                var value = ReadTaggedValue(reader, extendedFormat);

                return new RestrictionNode(RestrictionKind.Content)
                {
                    PropertyTag = propertyTag,
                    Value = value,
                    FuzzyLevelLow = fuzzyLow,
                    FuzzyLevelHigh = fuzzyHigh,
                    Detail = $"fuzzy={DescribeFuzzy(fuzzyLow, fuzzyHigh)}",
                };
            }

            case TypeProperty:
            {
                var relOp = reader.UInt8();
                var propertyTag = reader.UInt32();
                var value = ReadTaggedValue(reader, extendedFormat);

                return new RestrictionNode(RestrictionKind.Property)
                {
                    PropertyTag = propertyTag,
                    Value = value,
                    RelOp = relOp,
                    Detail = DescribeRelOp(relOp),
                };
            }

            case TypeBitMask:
            {
                var bitmapRelOp = reader.UInt8();
                var propertyTag = reader.UInt32();
                var mask = reader.UInt32();

                return new RestrictionNode(RestrictionKind.BitMask)
                {
                    PropertyTag = propertyTag,
                    RelOp = bitmapRelOp,
                    Mask = mask,
                    Detail = $"{(bitmapRelOp == 0 ? "BMR_EQZ" : "BMR_NEZ")} mask=0x{mask:X8}",
                };
            }

            case TypeSize:
            {
                var relOp = reader.UInt8();
                var propertyTag = reader.UInt32();
                var size = reader.UInt32();

                return new RestrictionNode(RestrictionKind.Size)
                {
                    PropertyTag = propertyTag,
                    RelOp = relOp,
                    Mask = size,
                    Detail = $"{DescribeRelOp(relOp)} {size}",
                };
            }

            case TypeExist:
            {
                var propertyTag = reader.UInt32();
                return new RestrictionNode(RestrictionKind.Exist) { PropertyTag = propertyTag };
            }

            case TypeSubObject:
            {
                var subobject = reader.UInt32();
                return new RestrictionNode(RestrictionKind.SubObject)
                {
                    PropertyTag = subobject,
                    Children = [Decode(reader, extendedFormat)],
                };
            }

            case TypeComment:
            {
                // RES_COMMENT (MS-OXCDATA 2.12.11): TaggedValueCount (1), TaggedValues, RestrictionPresent (1),
                // Restriction. Outlook wraps its address predicates in one, carrying the display name and
                // address for its own UI; the server evaluates only the inner restriction.
                var valueCount = reader.UInt8();
                var values = new List<TaggedPropertyValue>(valueCount);
                for (var i = 0; i < valueCount; i++)
                {
                    var tag = reader.UInt32();
                    values.Add(new TaggedPropertyValue(tag, ReadValue(reader, tag, extendedFormat)));
                }

                var present = reader.UInt8() != 0;
                return new RestrictionNode(RestrictionKind.Comment)
                {
                    CommentValues = values,
                    Children = present ? [Decode(reader, extendedFormat)] : [],
                };
            }

            case TypeCount:
            {
                var count = reader.UInt32();
                return new RestrictionNode(RestrictionKind.Count)
                {
                    Mask = count,
                    Detail = count.ToString(),
                    Children = [Decode(reader, extendedFormat)],
                };
            }

            default:
                throw new MapiFormatException($"Unhandled restriction type 0x{type:X2} at offset {reader.Position - 1}.");
        }
    }

    /// <summary>
    /// Encodes a restriction for a ROP buffer (narrow form) or an extended rule (wide form). The inverse
    /// of <see cref="Decode"/> for every kind it produces.
    /// </summary>
    public static void Encode(RopWriter writer, RestrictionNode node, bool extendedFormat = false)
    {
        switch (node.Kind)
        {
            case RestrictionKind.And:
            case RestrictionKind.Or:
                writer.UInt8(node.Kind == RestrictionKind.And ? TypeAnd : TypeOr);
                if (extendedFormat) writer.UInt32((uint)node.Children.Count); else writer.UInt16((ushort)node.Children.Count);
                foreach (var child in node.Children)
                {
                    Encode(writer, child, extendedFormat);
                }
                break;

            case RestrictionKind.Not:
                writer.UInt8(TypeNot);
                Encode(writer, RequireChild(node), extendedFormat);
                break;

            case RestrictionKind.Content:
            {
                var tag = RequireTag(node);
                writer.UInt8(TypeContent);
                writer.UInt16(node.FuzzyLevelLow);
                writer.UInt16(node.FuzzyLevelHigh);
                writer.UInt32(tag);
                WriteTaggedValue(writer, tag, node.Value, extendedFormat);
                break;
            }

            case RestrictionKind.Property:
            {
                var tag = RequireTag(node);
                writer.UInt8(TypeProperty);
                writer.UInt8(node.RelOp);
                writer.UInt32(tag);
                WriteTaggedValue(writer, tag, node.Value, extendedFormat);
                break;
            }

            case RestrictionKind.BitMask:
                writer.UInt8(TypeBitMask);
                writer.UInt8(node.RelOp);
                writer.UInt32(RequireTag(node));
                writer.UInt32(node.Mask);
                break;

            case RestrictionKind.Size:
                writer.UInt8(TypeSize);
                writer.UInt8(node.RelOp);
                writer.UInt32(RequireTag(node));
                writer.UInt32(node.Mask);
                break;

            case RestrictionKind.Exist:
                writer.UInt8(TypeExist);
                writer.UInt32(RequireTag(node));
                break;

            case RestrictionKind.SubObject:
                writer.UInt8(TypeSubObject);
                writer.UInt32(RequireTag(node));
                Encode(writer, RequireChild(node), extendedFormat);
                break;

            case RestrictionKind.Comment:
                writer.UInt8(TypeComment);
                writer.UInt8((byte)node.CommentValues.Count);
                foreach (var value in node.CommentValues)
                {
                    value.WriteTo(writer);
                }
                writer.UInt8(node.Children.Count > 0 ? (byte)1 : (byte)0);
                if (node.Children.Count > 0)
                    Encode(writer, node.Children[0], extendedFormat);
                break;

            case RestrictionKind.Count:
                writer.UInt8(TypeCount);
                writer.UInt32(node.Mask);
                Encode(writer, RequireChild(node), extendedFormat);
                break;

            default:
                throw new MapiFormatException($"Cannot encode restriction kind {node.Kind}.");
        }
    }

    private static RestrictionNode RequireChild(RestrictionNode node)
        => node.Children.Count == 1 ? node.Children[0] : throw new MapiFormatException($"{node.Kind} needs exactly one child.");

    private static uint RequireTag(RestrictionNode node)
        => node.PropertyTag ?? throw new MapiFormatException($"{node.Kind} needs a property tag.");

    /// <summary>TaggedPropertyValue: the tag repeated, then the value in its declared type.</summary>
    private static object ReadTaggedValue(RopReader reader, bool extendedFormat)
    {
        var tag = reader.UInt32();
        return ReadValue(reader, tag, extendedFormat);
    }

    /// <summary>Counted values (binaries, multi-values) carry 32-bit counts in the extended form.</summary>
    private static object ReadValue(RopReader reader, uint tag, bool extendedFormat)
    {
        var type = (ushort)(tag & 0xFFFF);
        if (extendedFormat && type == PropertyTypes.Binary)
            return reader.Bytes((int)reader.UInt32()).ToArray();

        return PropertyRow.ReadValue(reader, type);
    }

    private static void WriteTaggedValue(RopWriter writer, uint tag, object? value, bool extendedFormat)
    {
        if (value is null)
            throw new MapiFormatException($"Restriction on 0x{tag:X8} has no value.");

        if (extendedFormat && value is byte[] bytes)
        {
            writer.UInt32(tag);
            writer.UInt32((uint)bytes.Length);
            writer.Bytes(bytes);
            return;
        }

        new TaggedPropertyValue(tag, value).WriteTo(writer);
    }

    private static string DescribeFuzzy(ushort low, ushort high)
    {
        var parts = new List<string>
        {
            (low & 0x0003) switch
            {
                0 => "FULLSTRING",
                1 => "SUBSTRING",
                2 => "PREFIX",
                _ => $"level{low & 0x0003}",
            },
        };

        if ((high & 0x0001) != 0) parts.Add("IGNORECASE");
        if ((high & 0x0002) != 0) parts.Add("IGNORENONSPACE");
        if ((high & 0x0004) != 0) parts.Add("LOOSE");

        return string.Join("|", parts);
    }

    private static string DescribeRelOp(byte relOp) => relOp switch
    {
        0x00 => "LT",
        0x01 => "LE",
        0x02 => "GT",
        0x03 => "GE",
        0x04 => "EQ",
        0x05 => "NE",
        0x06 => "RE",
        _ => $"relop0x{relOp:X2}",
    };
}

public enum RestrictionKind
{
    And,
    Or,
    Not,
    Content,
    Property,
    BitMask,
    Size,
    Exist,
    SubObject,
    Comment,
    Count,
}

/// <summary>Relational operators (MS-OXCDATA 2.12.5.1) and fuzzy-level bits (2.12.4.1).</summary>
public static class RestrictionOps
{
    public const byte RelOpLt = 0x00;
    public const byte RelOpLe = 0x01;
    public const byte RelOpGt = 0x02;
    public const byte RelOpGe = 0x03;
    public const byte RelOpEq = 0x04;
    public const byte RelOpNe = 0x05;

    public const byte BitMaskEqZ = 0x00;
    public const byte BitMaskNeZ = 0x01;

    public const ushort FuzzyFullString = 0x0000;
    public const ushort FuzzySubString = 0x0001;
    public const ushort FuzzyPrefix = 0x0002;
    public const ushort FuzzyIgnoreCase = 0x0001;
    public const ushort FuzzyIgnoreNonSpace = 0x0002;
    public const ushort FuzzyLoose = 0x0004;
}

public sealed class RestrictionNode(RestrictionKind kind)
{
    public RestrictionKind Kind { get; } = kind;
    public uint? PropertyTag { get; init; }
    public object? Value { get; init; }
    public string? Detail { get; init; }
    public List<RestrictionNode> Children { get; init; } = [];
    public ushort NamedPropertyCount { get; set; }

    /// <summary>Content: FuzzyLevelLow (match kind) and FuzzyLevelHigh (case/space flags).</summary>
    public ushort FuzzyLevelLow { get; init; }
    public ushort FuzzyLevelHigh { get; init; }

    /// <summary>Property and Size: the relational operator; BitMask: BMR_EQZ or BMR_NEZ.</summary>
    public byte RelOp { get; init; }

    /// <summary>BitMask: the mask; Size: the size; Count: the count.</summary>
    public uint Mask { get; init; }

    /// <summary>Comment: the tagged values Outlook stores for its own UI.</summary>
    public List<TaggedPropertyValue> CommentValues { get; init; } = [];

    // ---- Builders -------------------------------------------------------------------------------

    public static RestrictionNode And(params RestrictionNode[] children) => new(RestrictionKind.And) { Children = [.. children] };
    public static RestrictionNode Or(params RestrictionNode[] children) => new(RestrictionKind.Or) { Children = [.. children] };
    public static RestrictionNode Not(RestrictionNode child) => new(RestrictionKind.Not) { Children = [child] };

    public static RestrictionNode Content(uint propertyTag, object value, ushort fuzzyLow = RestrictionOps.FuzzySubString, ushort fuzzyHigh = RestrictionOps.FuzzyIgnoreCase)
        => new(RestrictionKind.Content) { PropertyTag = propertyTag, Value = value, FuzzyLevelLow = fuzzyLow, FuzzyLevelHigh = fuzzyHigh };

    public static RestrictionNode Property(byte relOp, uint propertyTag, object value)
        => new(RestrictionKind.Property) { PropertyTag = propertyTag, Value = value, RelOp = relOp };

    public static RestrictionNode BitMask(byte bitMaskRelOp, uint propertyTag, uint mask)
        => new(RestrictionKind.BitMask) { PropertyTag = propertyTag, RelOp = bitMaskRelOp, Mask = mask };

    public static RestrictionNode Exist(uint propertyTag) => new(RestrictionKind.Exist) { PropertyTag = propertyTag };

    public static RestrictionNode SubObject(uint propertyTag, RestrictionNode child)
        => new(RestrictionKind.SubObject) { PropertyTag = propertyTag, Children = [child] };

    public static RestrictionNode Comment(IReadOnlyList<TaggedPropertyValue> values, RestrictionNode child)
        => new(RestrictionKind.Comment) { CommentValues = [.. values], Children = [child] };

    /// <summary>Renders the tree. Values are included: callers that log must mask first.</summary>
    public override string ToString() => Describe(0);

    private string Describe(int indent)
    {
        var pad = new string(' ', indent * 2);
        var builder = new StringBuilder();

        builder.Append(pad).Append(Kind.ToString().ToUpperInvariant());

        if (PropertyTag is { } tag)
        {
            builder.Append(' ').Append(MapiProperties.Describe(tag));
        }

        if (Detail is not null)
        {
            builder.Append(" [").Append(Detail).Append(']');
        }

        if (Value is not null)
        {
            builder.Append(" = ").Append(Value is string s ? $"\"{s}\"" : Value);
        }

        foreach (var child in Children)
        {
            builder.AppendLine().Append(child.Describe(indent + 1));
        }

        return builder.ToString();
    }
}

/// <summary>Just enough of the property dictionary to make decoded rules readable.</summary>
public static class MapiProperties
{
    private static readonly Dictionary<ushort, string> Known = new()
    {
        [0x0017] = "PidTagImportance",
        [0x001A] = "PidTagMessageClass",
        [0x0037] = "PidTagSubject",
        [0x003D] = "PidTagSubjectPrefix",
        [0x0075] = "PidTagReceivedByName",
        [0x0076] = "PidTagReceivedByEmailAddress",
        [0x0C1A] = "PidTagSenderName",
        [0x0C1D] = "PidTagSenderSearchKey",
        [0x0C1F] = "PidTagSenderEmailAddress",
        [0x0E03] = "PidTagDisplayCc",
        [0x0E04] = "PidTagDisplayTo",
        [0x0E06] = "PidTagMessageDeliveryTime",
        [0x0E07] = "PidTagMessageFlags",
        [0x0E08] = "PidTagMessageSize",
        [0x0E12] = "PidTagMessageRecipients",
        [0x0E1D] = "PidTagNormalizedSubject",
        [0x1000] = "PidTagBody",
        [0x300B] = "PidTagSearchKey",
        [0x4076] = "PidTagContentFilterSpamConfidenceLevel",
        [0x5D01] = "PidTagSenderSmtpAddress",
    };

    public static string Describe(uint tag)
    {
        var id = (ushort)(tag >> 16);
        var name = Known.TryGetValue(id, out var known) ? known : $"0x{id:X4}";
        return $"{name}(0x{tag:X8})";
    }
}

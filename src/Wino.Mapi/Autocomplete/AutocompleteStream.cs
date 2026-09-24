using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Mapi.Autocomplete;

/// <summary>
/// Outlook's autocomplete list - the names that appear under the To box as you type - as it is
/// stored in the mailbox.
///
/// Outlook 2007 kept this in an .nk2 file beside the profile. From 2010 it lives in the mailbox, on
/// a hidden message in the Inbox's associated contents table, and the list itself is one binary
/// property on that message. The layout:
///
///     metadata          4 bytes, meaning nothing to us
///     major version     4 bytes, must be 12
///     minor version     4 bytes
///     row count         4 bytes
///       per row:        property count, then that many properties
///       per property:   tag (4), reserved (4), value union (8), value data (0 or more)
///     extra length      4 bytes
///     extra             that many bytes, belonging to whichever Outlook wrote them
///     metadata          8 bytes, again not ours
///
/// Two rules come with the format and both are obeyed here. The parts that are not the row set are
/// carried through untouched, because a newer Outlook may have written something into them that it
/// expects to find again. And a row is only ever replaced whole - properties are kept exactly as
/// they arrived, as raw bytes, so a row nobody edited serialises back byte for byte. That is what
/// makes it safe to write a list back into somebody's mailbox: the parts we did not understand are
/// returned unchanged rather than dropped.
/// </summary>
public sealed class AutocompleteStream
{
    /// <summary>The only major version this format has had, and the only one safe to touch.</summary>
    public const uint SupportedMajorVersion = 12;

    private const int HeaderSize = 12;
    private const int TrailingMetadataSize = 8;

    private AutocompleteStream(uint metadata, uint minorVersion, byte[] extraInformation, byte[] trailingMetadata, List<AutocompleteEntry> entries)
    {
        Metadata = metadata;
        MinorVersion = minorVersion;
        ExtraInformation = extraInformation;
        TrailingMetadata = trailingMetadata;
        Entries = entries;
    }

    /// <summary>
    /// Whether this list, as read and before anything was changed, serialises back to the exact
    /// bytes it came from.
    ///
    /// It is checked on the way in and remembered, because it is the licence to write. Every test
    /// this parser has is against streams the same code produced, which proves it is self-consistent
    /// and not that it understands what a particular Exchange actually stores. A mailbox whose list
    /// this code cannot reproduce untouched is one it has no business writing to, and the writer
    /// refuses rather than finding out afterwards.
    ///
    /// True for a list that was never read from anywhere, which has nothing to contradict.
    /// </summary>
    public bool RoundTripsExactly { get; private set; } = true;

    /// <summary>The leading four bytes, kept as they were found.</summary>
    public uint Metadata { get; }

    public uint MinorVersion { get; }

    /// <summary>Whatever a newer Outlook wrote after the rows. Returned unchanged.</summary>
    public byte[] ExtraInformation { get; }

    /// <summary>The trailing eight bytes, kept as they were found.</summary>
    public byte[] TrailingMetadata { get; }

    /// <summary>The list, in the order it is stored: heaviest first.</summary>
    public List<AutocompleteEntry> Entries { get; }

    /// <summary>An empty list, for a mailbox that has never had one.</summary>
    public static AutocompleteStream Empty() => new(0, 0, [], new byte[TrailingMetadataSize], []);

    /// <summary>
    /// Reads a stream. Throws <see cref="AutocompleteFormatException"/> when the bytes are not a
    /// list this code should touch - a different major version included, which the format says to
    /// leave alone rather than guess at.
    /// </summary>
    public static AutocompleteStream Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize + 4 + 4 + TrailingMetadataSize)
            throw new AutocompleteFormatException("The autocomplete stream is too short to be one.");

        var metadata = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var major = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var minor = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);

        if (major != SupportedMajorVersion)
            throw new AutocompleteFormatException($"The autocomplete stream is major version {major}; only {SupportedMajorVersion} is understood.");

        var offset = HeaderSize;
        var rowCount = ReadUInt32(bytes, ref offset);

        var entries = new List<AutocompleteEntry>((int)Math.Min(rowCount, 4096));

        for (var row = 0u; row < rowCount; row++)
        {
            var propertyCount = ReadUInt32(bytes, ref offset);
            var properties = new List<AutocompleteProperty>((int)Math.Min(propertyCount, 64));

            for (var i = 0u; i < propertyCount; i++)
                properties.Add(AutocompleteProperty.Read(bytes, ref offset));

            entries.Add(new AutocompleteEntry(properties));
        }

        var extraLength = ReadUInt32(bytes, ref offset);
        var extra = Take(bytes, ref offset, checked((int)extraLength)).ToArray();
        var trailing = Take(bytes, ref offset, TrailingMetadataSize).ToArray();

        var stream = new AutocompleteStream(metadata, minor, extra, trailing, entries);
        stream.RoundTripsExactly = stream.Serialize().AsSpan().SequenceEqual(bytes);

        return stream;
    }

    /// <summary>Writes the stream back out, in the shape it was read in.</summary>
    public byte[] Serialize()
    {
        var output = new List<byte>(4096);

        Append(output, Metadata);
        Append(output, SupportedMajorVersion);
        Append(output, MinorVersion);
        Append(output, (uint)Entries.Count);

        foreach (var entry in Entries)
        {
            Append(output, (uint)entry.Properties.Count);

            foreach (var property in entry.Properties)
                property.Write(output);
        }

        Append(output, (uint)ExtraInformation.Length);
        output.AddRange(ExtraInformation);
        output.AddRange(TrailingMetadata);

        return [.. output];
    }

    /// <summary>
    /// Notes that a message went to this address: a new entry if it is new, and a heavier one if it
    /// is not, which is how the list ends up ordered by who you actually write to. Outlook moves a
    /// weight by 0x2000 each time, and the list is kept sorted heaviest first because the format
    /// says it is stored that way.
    /// </summary>
    public void Record(string smtpAddress, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(smtpAddress))
            return;

        var at = Entries.FindIndex(e => string.Equals(e.SmtpAddress, smtpAddress, StringComparison.OrdinalIgnoreCase));

        AutocompleteEntry entry;

        if (at < 0)
        {
            entry = AutocompleteEntry.Create(smtpAddress, displayName);
        }
        else
        {
            entry = Entries[at];
            entry.Weight = AutocompleteEntry.Heavier(entry.Weight);
            Entries.RemoveAt(at);
        }

        // One entry moved in an already-sorted list is an insertion, not a re-sort. Sorting here
        // instead cost a comparison per pair, and every comparison read Weight - which is a linear
        // scan of the row's properties - so a send to five people re-sorted the list five times and
        // walked those properties tens of thousands of times, on the UI thread, on the Send click.
        var weight = entry.Weight;
        var insertAt = Entries.FindIndex(e => e.Weight < weight);

        Entries.Insert(insertAt < 0 ? Entries.Count : insertAt, entry);
    }

    /// <summary>Forgets an address, for somebody removing a name from the list.</summary>
    public bool Forget(string smtpAddress)
        => Entries.RemoveAll(e => string.Equals(e.SmtpAddress, smtpAddress, StringComparison.OrdinalIgnoreCase)) > 0;

    private static void Append(List<byte> output, uint value)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(four, value);
        output.AddRange(four);
    }

    internal static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(Take(bytes, ref offset, 4));

    internal static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int offset, int count)
    {
        if (count < 0 || offset + count > bytes.Length)
            throw new AutocompleteFormatException("The autocomplete stream ends in the middle of a value.");

        var slice = bytes.Slice(offset, count);
        offset += count;

        return slice;
    }
}

/// <summary>The bytes are not an autocomplete stream, or not one this code should write back.</summary>
public sealed class AutocompleteFormatException(string message) : MapiException(message);

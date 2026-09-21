using Wino.Mapi;
using Wino.Mapi.Ics;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// The ICS codecs: serialized IDSETs and the FastTransfer element encoding. Both are
/// "plausible number, not an error" territory, so every command and every widened count is pinned.
/// </summary>
public class MapiIcsTests
{
    private static readonly Guid Repl = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void IdSet_PushPopBitmaskRange_YieldLongTermIds()
    {
        // One REPLGUID, then a GLOBSET:
        //   push 5 [00 00 00 00 01]      common prefix
        //   bitmask start 0x10, mask 0b00000101   -> 0x10, 0x11, 0x13
        //   range 0x20..0x22                       -> 0x20, 0x21, 0x22
        //   pop
        //   push 6 [00 00 00 00 02 FF]             -> a complete value
        //   end
        var writer = new RopWriter();
        writer.Bytes(Repl.ToByteArray());
        writer.UInt8(0x05); writer.Bytes([0, 0, 0, 0, 1]);
        writer.UInt8(0x42); writer.UInt8(0x10); writer.UInt8(0b00000101);
        writer.UInt8(0x52); writer.UInt8(0x20); writer.UInt8(0x22);
        writer.UInt8(0x50);
        writer.UInt8(0x06); writer.Bytes([0, 0, 0, 0, 2, 0xFF]);
        writer.UInt8(0x00);

        var ids = IdSet.DecodeLongTermIds(writer.ToArray());

        ids.Should().HaveCount(7);
        ids.Should().OnlyContain(id => id.Length == RopIds.LongTermIdLength && id.Take(16).SequenceEqual(Repl.ToByteArray()));
        ids.Select(id => id[21]).Should().Equal(0x10, 0x11, 0x13, 0x20, 0x21, 0x22, 0xFF);
        ids[0][20].Should().Be(1, "the pushed prefix");
        ids[6][20].Should().Be(2, "the full push after the pop");
    }

    [Fact]
    public void IdSet_TwoReplGuids()
    {
        var other = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        var writer = new RopWriter();
        writer.Bytes(Repl.ToByteArray()); writer.UInt8(0x06); writer.Bytes([0, 0, 0, 0, 0, 1]); writer.UInt8(0x00);
        writer.Bytes(other.ToByteArray()); writer.UInt8(0x06); writer.Bytes([0, 0, 0, 0, 0, 2]); writer.UInt8(0x00);

        var ids = IdSet.DecodeLongTermIds(writer.ToArray());

        ids.Should().HaveCount(2);
        ids[1].Take(16).Should().Equal(other.ToByteArray());
        ids[1][21].Should().Be(2);
    }

    /// <summary>
    /// The download stream's read/deleted sets use 2-byte REPLIDs (found live: a 10-byte IDSET).
    /// REPLID 0x0001 + counter 00 00 00 00 01 0C is the wire form of message id 0x0C01000000000001.
    /// </summary>
    [Fact]
    public void IdSet_ReplIdForm_YieldsMessageIdsDirectly()
    {
        var writer = new RopWriter();
        writer.UInt16(0x0001);                                       // REPLID
        writer.UInt8(0x06); writer.Bytes([0, 0, 0, 0, 1, 0x0C]);     // one full counter
        writer.UInt8(0x00);
        writer.ToArray().Should().HaveCount(10);

        IdSet.DecodeIds(writer.ToArray()).Should().Equal(0x0C01000000000001UL);
    }

    [Fact]
    public void FastTransfer_ElementsAcrossBufferBoundary()
    {
        // A minimal contents stream: IncrSyncChg, header (Mid), IncrSyncMessage, props, IncrSyncEnd.
        var writer = new RopWriter();
        writer.UInt32(FxTags.IncrSyncChg);
        writer.UInt32(PropertyTags.Mid); writer.UInt64(0x0001000000000001);
        writer.UInt32(FxTags.IncrSyncMessage);
        writer.UInt32(PropertyTags.Subject); WriteUnicode(writer, "Hello there");
        writer.UInt32(PropertyTags.MessageFlags); writer.UInt32(1);
        writer.UInt32(PropertyTags.HasAttachments); writer.UInt16(1);              // booleans are 2 bytes here
        writer.UInt32(PropertyTags.ConversationId); writer.UInt32(3); writer.Bytes([7, 8, 9]);
        writer.UInt32(FxTags.IncrSyncEnd);
        var stream = writer.ToArray();

        // Feed it in awkward pieces: the split lands inside the subject string and inside a tag.
        var reader = new FastTransferReader();
        var elements = new List<FxElement>();
        elements.AddRange(reader.Feed(stream.AsSpan(0, 21)));
        elements.AddRange(reader.Feed(stream.AsSpan(21, 10)));
        elements.AddRange(reader.Feed(stream.AsSpan(31)));
        reader.PendingBytes.Should().Be(0);

        elements.Select(e => e.Tag).Should().Equal(
            FxTags.IncrSyncChg, PropertyTags.Mid, FxTags.IncrSyncMessage, PropertyTags.Subject,
            PropertyTags.MessageFlags, PropertyTags.HasAttachments, PropertyTags.ConversationId, FxTags.IncrSyncEnd);
        elements[0].IsMarker.Should().BeTrue();
        elements[1].Value.Should().Be(0x0001000000000001UL);
        elements[3].Value.Should().Be("Hello there");
        elements[5].Value.Should().Be(true);
        elements[6].Value.Should().BeEquivalentTo(new byte[] { 7, 8, 9 });
    }

    [Fact]
    public void FastTransfer_MetaTagIdsetGivenIsCountedBinaryDespiteItsType()
    {
        var writer = new RopWriter();
        writer.UInt32(FxTags.IncrSyncStateBegin);
        writer.UInt32(FxTags.MetaTagIdsetGiven); writer.UInt32(4); writer.Bytes([1, 2, 3, 4]);
        writer.UInt32(FxTags.MetaTagCnsetSeen); writer.UInt32(2); writer.Bytes([5, 6]);
        writer.UInt32(FxTags.IncrSyncStateEnd);

        var elements = new FastTransferReader().Feed(writer.ToArray());

        elements.Should().HaveCount(4);
        elements[1].Value.Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4 });
        elements[2].Value.Should().BeEquivalentTo(new byte[] { 5, 6 });
    }

    [Fact]
    public void SyncConfigure_EncodesFlagsRestrictionAndTags()
    {
        var rop = RopSync.BuildSyncConfigure(1, 2, RopSync.SynchronizationType.Contents, RopSync.SendOptions.Unicode,
            RopSync.SynchronizationFlags.Unicode | RopSync.SynchronizationFlags.Normal | RopSync.SynchronizationFlags.ReadState | RopSync.SynchronizationFlags.OnlySpecifiedProperties,
            RopSync.SynchronizationExtraFlags.Eid | RopSync.SynchronizationExtraFlags.OrderByDeliveryTime,
            [PropertyTags.Subject, PropertyTags.MessageFlags]);

        var reader = new RopReader(rop);
        reader.UInt8().Should().Be(RopSync.RopSyncConfigure);
        reader.UInt8(); reader.UInt8().Should().Be(1); reader.UInt8().Should().Be(2);
        reader.UInt8().Should().Be(0x01, "Contents");
        reader.UInt8().Should().Be(0x01, "SendOptions Unicode");
        reader.UInt16().Should().Be(0x00A9, "Unicode|ReadState|Normal|OnlySpecifiedProperties");
        reader.UInt16().Should().Be(0, "no restriction");
        reader.UInt32().Should().Be(0x09, "Eid|OrderByDeliveryTime");
        reader.UInt16().Should().Be(2);
        reader.UInt32().Should().Be(PropertyTags.Subject);
        reader.UInt32().Should().Be(PropertyTags.MessageFlags);
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void FastTransferGetBuffer_ParsesDoneAndServerBusy()
    {
        var writer = new RopWriter();
        writer.UInt8(RopSync.RopFastTransferSourceGetBuffer); writer.UInt8(2); writer.UInt32(0);
        writer.UInt16((ushort)RopSync.TransferStatus.Done); writer.UInt16(1); writer.UInt16(1); writer.UInt8(0);
        writer.UInt16(3); writer.Bytes([0xAA, 0xBB, 0xCC]);

        var done = RopSync.ParseFastTransferSourceGetBuffer(new RopReader(writer.ToArray()));
        done.Status.Should().Be(RopSync.TransferStatus.Done);
        done.Data.Should().Equal(0xAA, 0xBB, 0xCC);
        done.BackoffMilliseconds.Should().BeNull();

        var busy = new RopWriter();
        busy.UInt8(RopSync.RopFastTransferSourceGetBuffer); busy.UInt8(2); busy.UInt32(RopSync.ServerBusy);
        busy.UInt16(0); busy.UInt16(0); busy.UInt16(0); busy.UInt8(0); busy.UInt16(0); busy.UInt32(1500);

        var backoff = RopSync.ParseFastTransferSourceGetBuffer(new RopReader(busy.ToArray()));
        backoff.BackoffMilliseconds.Should().Be(1500);
    }

    [Fact]
    public void IcsState_RoundTripsThroughJson()
    {
        var state = new IcsState { IdsetGiven = [1, 2], CnsetSeen = [3], CnsetRead = [4, 5, 6] };
        var text = state.Serialize();
        var back = IcsState.Deserialize(text)!;
        back.IdsetGiven.Should().Equal(1, 2);
        back.CnsetSeenFAI.Should().BeNull();
        back.CnsetRead.Should().Equal(4, 5, 6);
        IcsState.Deserialize("some-old-ews-sync-state==").Should().BeNull("a non-JSON token is not an ICS state");
    }

    private static void WriteUnicode(RopWriter writer, string text)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
        writer.UInt32((uint)bytes.Length);
        writer.Bytes(bytes);
    }
}

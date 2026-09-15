using Wino.Mapi;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// The message-side ROPs (rungs 2 to 4): request encodings checked field by field against MS-OXCROPS,
/// and the list-row / attachment-row parsing the synchronizer maps from. Requests are checked by
/// reading them back, because a wrong field width here fails on the server as ecRpcFormat, which
/// says nothing about which field.
/// </summary>
public class MapiMessageRopTests
{
    [Fact]
    public void SortTable_EncodesTypeThenIdThenDescending()
    {
        var rop = RopFolder.BuildSortTable(2, [new RopFolder.SortOrder(PropertyTags.MessageDeliveryTime, Descending: true)]);

        var reader = new RopReader(rop);
        reader.UInt8().Should().Be(RopFolder.RopSortTable);
        reader.UInt8().Should().Be(0, "LogonId");
        reader.UInt8().Should().Be(2, "table handle index");
        reader.UInt8().Should().Be(0, "synchronous");
        reader.UInt16().Should().Be(1, "SortOrderCount");
        reader.UInt16().Should().Be(0, "CategoryCount");
        reader.UInt16().Should().Be(0, "ExpandedCount");
        reader.UInt16().Should().Be(0x0040, "PropertyType PT_SYSTIME comes first");
        reader.UInt16().Should().Be(0x0E06, "then PropertyId");
        reader.UInt8().Should().Be(1, "descending");
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void SetReadFlags_MoveCopy_Delete_EncodeMessageIdLists()
    {
        ulong[] ids = [0x1111000000000001, 0x2222000000000001];

        var read = new RopReader(RopMessageOps.BuildSetReadFlags(1, RopMessageOps.ReadFlags.ClearReadFlag, ids));
        read.UInt8().Should().Be(RopMessageOps.RopSetReadFlags);
        read.UInt8(); read.UInt8().Should().Be(1, "folder handle");
        read.UInt8().Should().Be(0, "WantAsynchronous");
        read.UInt8().Should().Be(0x04, "ClearReadFlag");
        read.UInt16().Should().Be(2);
        read.UInt64().Should().Be(ids[0]); read.UInt64().Should().Be(ids[1]);
        read.Remaining.Should().Be(0);

        var move = new RopReader(RopMessageOps.BuildMoveCopyMessages(1, 2, ids, copy: false));
        move.UInt8().Should().Be(RopMessageOps.RopMoveCopyMessages);
        move.UInt8(); move.UInt8().Should().Be(1, "source"); move.UInt8().Should().Be(2, "destination");
        move.UInt16().Should().Be(2);
        move.UInt64(); move.UInt64();
        move.UInt8().Should().Be(0, "WantAsynchronous");
        move.UInt8().Should().Be(0, "WantCopy");
        move.Remaining.Should().Be(0);

        var delete = new RopReader(RopMessageOps.BuildDeleteMessages(1, ids));
        delete.UInt8().Should().Be(RopMessageOps.RopDeleteMessages);
        delete.UInt8(); delete.UInt8().Should().Be(1);
        delete.UInt8().Should().Be(0, "WantAsynchronous");
        delete.UInt8().Should().Be(0, "NotifyNonRead");
        delete.UInt16().Should().Be(2);
        delete.Bytes(16);
        delete.Remaining.Should().Be(0);
    }

    [Fact]
    public void SetProperties_EncodesSizeCountAndTaggedValues()
    {
        var rop = RopMessageOps.BuildSetProperties(2, [
            TaggedPropertyValue.Long(PropertyTags.FlagStatus, 2),
            TaggedPropertyValue.Unicode(PropertyTags.Subject, "Hi"),
            TaggedPropertyValue.Boolean(PropertyTags.ReadReceiptRequested, true),
        ]);

        var reader = new RopReader(rop);
        reader.UInt8().Should().Be(RopMessageOps.RopSetProperties);
        reader.UInt8(); reader.UInt8().Should().Be(2);
        var size = reader.UInt16();
        reader.Remaining.Should().Be(size, "PropertyValueSize covers the count and the values");
        reader.UInt16().Should().Be(3, "PropertyValueCount");
        reader.UInt32().Should().Be(PropertyTags.FlagStatus); reader.UInt32().Should().Be(2);
        reader.UInt32().Should().Be(PropertyTags.Subject); reader.UnicodeZ().Should().Be("Hi");
        reader.UInt32().Should().Be(PropertyTags.ReadReceiptRequested); reader.UInt8().Should().Be(1);
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void SaveChangesMessage_ResponseCarriesMessageId()
    {
        var writer = new RopWriter();
        writer.UInt8(RopMessageOps.RopSaveChangesMessage); writer.UInt8(3); writer.UInt32(0);
        writer.UInt8(2);                                   // InputHandleIndex echoed
        writer.UInt64(0xABCD000000000001);

        RopMessageOps.ParseSaveChangesMessage(new RopReader(writer.ToArray())).Should().Be(0xABCD000000000001);
    }

    [Fact]
    public void SetPropertiesResponse_ListsProblems()
    {
        var writer = new RopWriter();
        writer.UInt8(RopMessageOps.RopSetProperties); writer.UInt8(2); writer.UInt32(0);
        writer.UInt16(1);                                  // PropertyProblemCount
        writer.UInt16(0); writer.UInt32(PropertyTags.FlagStatus); writer.UInt32(MapiRopException.AccessDenied);

        var problems = RopMessageOps.ParseSetProperties(new RopReader(writer.ToArray()));

        problems.Should().ContainSingle().Which.Should().Be((PropertyTags.FlagStatus, MapiRopException.AccessDenied));
    }

    /// <summary>A list row with every column present, then one with the flagged shape and a few absent cells.</summary>
    [Fact]
    public void MessageListRow_MapsToMessageInfo()
    {
        var columns = PropertyTags.MessageListColumns;
        var received = new DateTime(2026, 9, 3, 14, 30, 0, DateTimeKind.Utc);

        var writer = new RopWriter();
        writer.UInt8(RopFolder.RopQueryRows); writer.UInt8(2); writer.UInt32(0);
        writer.UInt8(0); writer.UInt16(1);
        writer.UInt8(0x01);                                // flagged row
        Cell(writer, 0x00); writer.UInt64(0x1234000000000001);              // Mid
        Cell(writer, 0x00); writer.UnicodeZ("IPM.Note");                     // MessageClass
        Cell(writer, 0x00); writer.UnicodeZ("Re: Orders 9616");             // Subject
        Cell(writer, 0x00); writer.UnicodeZ("Orders 9616");                 // NormalizedSubject
        Cell(writer, 0x00); writer.UnicodeZ("Troy Compton");                // SentRepresentingName
        Cell(writer, 0x00); writer.UnicodeZ("troy@example.com");            // SentRepresentingSmtpAddress
        Cell(writer, 0x01);                                                  // SenderName absent
        Cell(writer, 0x01);                                                  // SenderSmtpAddress absent
        Cell(writer, 0x00); writer.UInt64(unchecked((ulong)received.AddMinutes(-1).ToFileTimeUtc())); // ClientSubmitTime
        Cell(writer, 0x00); writer.UInt64(unchecked((ulong)received.ToFileTimeUtc()));                // MessageDeliveryTime
        Cell(writer, 0x00); writer.UInt64(unchecked((ulong)received.ToFileTimeUtc()));                // LastModificationTime
        Cell(writer, 0x00); writer.UInt32(PropertyTags.MessageFlagRead | PropertyTags.MessageFlagHasAttach); // MessageFlags
        Cell(writer, 0x00); writer.UInt32(48213);                            // MessageSize
        Cell(writer, 0x00); writer.UInt8(1);                                 // HasAttachments
        Cell(writer, 0x00); writer.UInt32(2);                                // Importance high
        Cell(writer, 0x00); writer.UInt32(2);                                // FlagStatus flagged
        Cell(writer, 0x00); writer.UInt16(3); writer.Bytes([1, 2, 3]);       // ConversationId
        Cell(writer, 0x00); writer.UnicodeZ("<abc@example.com>");           // InternetMessageId
        Cell(writer, 0x0A); writer.UInt32(MapiRopException.NotFound);        // InReplyToId error
        Cell(writer, 0x01);                                                  // InternetReferences absent
        Cell(writer, 0x00); writer.UnicodeZ("Matt; Someone");               // DisplayTo
        Cell(writer, 0x00); writer.UInt16(2); writer.Bytes([9, 9]);          // ChangeKey

        var row = RopFolder.ParseQueryRows(new RopReader(writer.ToArray()), columns).Single();
        var info = new MapiMessageInfo(row[0].AsUInt64!.Value, row);

        info.MessageId.Should().Be(0x1234000000000001);
        info.Subject.Should().Be("Re: Orders 9616");
        info.FromName.Should().Be("Troy Compton");
        info.FromAddress.Should().Be("troy@example.com");
        info.ReceivedTime.Should().Be(received);
        info.IsRead.Should().BeTrue();
        info.HasAttachments.Should().BeTrue();
        info.Size.Should().Be(48213);
        info.Importance.Should().Be(2);
        info.IsFlagged.Should().BeTrue();
        info.ConversationId.Should().Equal(1, 2, 3);
        info.InternetMessageId.Should().Be("<abc@example.com>");
        info.InReplyTo.Should().BeNull("an error cell reads as absent");
        info.DisplayTo.Should().Be("Matt; Someone");
    }

    [Fact]
    public void OpenMessage_ReadWriteFlagIsEncoded()
    {
        var reader = new RopReader(RopMessage.BuildOpenMessage(0x0C01000000000001, 0x0001000000000001, 1, 2, RopMessageOps.OpenReadWrite));
        reader.Bytes(4); reader.UInt16().Should().Be(0x0FFF, "CodePageId");
        reader.UInt64(); reader.UInt8().Should().Be(0x01, "OpenModeFlags read/write");
        reader.UInt64(); reader.Remaining.Should().Be(0);
    }

    /// <summary>
    /// The draft bridge: a message EntryID (70 bytes) carries the folder's long-term id then the
    /// message's; RopIdFromLongTermId takes the latter (22 bytes + 2 padding) and answers the Mid.
    /// </summary>
    [Fact]
    public void IdFromLongTermId_RoundTripsThroughMessageEntryId()
    {
        var folderDb = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var messageDb = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        byte[] folderCounter = [0, 0, 0, 0, 1, 0x0C];
        byte[] messageCounter = [0, 0, 0, 0, 2, 0xEE];

        var entryId = new RopWriter();
        entryId.UInt32(0);                                              // Flags
        entryId.Bytes(Guid.NewGuid().ToByteArray());                    // ProviderUID
        entryId.UInt16(0x0007);                                         // MessageType: private message
        entryId.Bytes(folderDb.ToByteArray()); entryId.Bytes(folderCounter); entryId.UInt16(0);
        entryId.Bytes(messageDb.ToByteArray()); entryId.Bytes(messageCounter); entryId.UInt16(0);
        entryId.Length.Should().Be(70);

        var longTermId = RopIds.MessageLongTermIdFromEntryId(entryId.ToArray());
        longTermId.Should().Equal(messageDb.ToByteArray().Concat(messageCounter));

        var request = new RopReader(RopIds.BuildIdFromLongTermId(0, longTermId));
        request.UInt8().Should().Be(RopIds.RopIdFromLongTermId);
        request.UInt8(); request.UInt8().Should().Be(0, "logon handle");
        request.Bytes(22).ToArray().Should().Equal(longTermId);
        request.UInt16().Should().Be(0, "padding");
        request.Remaining.Should().Be(0);

        var response = new RopWriter();
        response.UInt8(RopIds.RopIdFromLongTermId); response.UInt8(0); response.UInt32(0);
        response.UInt64(0xEE02000000000001);
        RopIds.ParseIdFromLongTermId(new RopReader(response.ToArray())).Should().Be(0xEE02000000000001);
    }

    private static void Cell(RopWriter writer, byte flag) => writer.UInt8(flag);
}

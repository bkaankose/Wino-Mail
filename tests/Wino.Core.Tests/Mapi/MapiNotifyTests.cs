using Wino.Mapi.Rops;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>Rung 7: the notification subscription request and the RopNotify response shapes (MS-OXCNOTIF 2.2.1.4.1).</summary>
public class MapiNotifyTests
{
    [Fact]
    public void RegisterNotification_WholeStore_Encodes()
    {
        var reader = new RopReader(RopNotify.BuildRegisterNotification(0, 1, RopNotify.NotificationTypes.StoreChanges));

        reader.UInt8().Should().Be(RopNotify.RopRegisterNotification);
        reader.UInt8().Should().Be(0, "LogonId");
        reader.UInt8().Should().Be(0, "logon handle");
        reader.UInt8().Should().Be(1, "subscription handle slot");
        reader.UInt16().Should().Be((ushort)RopNotify.NotificationTypes.StoreChanges);
        reader.UInt8().Should().Be(1, "WantWholeStore follows the types directly: Reserved exists only with TableModified");
        reader.Remaining.Should().Be(0, "no FolderId/MessageId when the whole store is wanted");
    }

    /// <summary>
    /// The exact buffer Exchange 2019 returned for one new mail (ids only): message Created in the Inbox
    /// (no ParentFolderId), the same Created seen through two search folders (S set, ParentFolderId
    /// present), NewMail with an ASCII message class, then three folder Modified events without counts.
    /// This is the shape the prose of MS-OXCNOTIF makes easy to get backwards.
    /// </summary>
    [Fact]
    public void ParseNotifications_LiveNewMailBuffer()
    {
        // Verbatim from the client log's raw dump (186 bytes).
        var hex =
            "2a01000000000480010000000000010c010000003833a37500002a0100000000" +
            "04c0010000000000010c010000003833a3750100000000001af100002a010000" +
            "000004c0010000000000010c010000003833a37501000000030f314800002a01" +
            "000000000280010000000000010c010000003833a375000000000049504d2e4e" +
            "4f5445002a01000000001000010000000000010c00002a010000000010000100" +
            "000000001af100002a0100000000100001000000030f31480000";

        var bytes = Convert.FromHexString(hex);
        bytes.Should().HaveCount(186);

        var notifications = RopNotify.ParseNotifications(bytes);

        notifications.Should().HaveCount(7);
        notifications[0].Type.Should().Be(RopNotify.NotificationTypes.ObjectCreated);
        notifications[0].IsMessage.Should().BeTrue();
        notifications[0].FolderId.Should().Be(0x0C01000000000001);
        notifications[0].ParentFolderId.Should().BeNull("a plain message event carries no parent");
        notifications[1].ParentFolderId.Should().Be(0xF11A000000000001, "seen through a search folder");
        notifications[2].ParentFolderId.Should().Be(0x48310F0300000001);
        notifications[3].Type.Should().Be(RopNotify.NotificationTypes.NewMail);
        notifications[4].Type.Should().Be(RopNotify.NotificationTypes.ObjectModified);
        notifications[4].IsMessage.Should().BeFalse();
        notifications[4].FolderId.Should().Be(0x0C01000000000001);
        notifications[6].FolderId.Should().Be(0x48310F0300000001);
    }

    [Fact]
    public void ParseNotifications_NewMailThenMessageModifiedThenFolderCreated()
    {
        var writer = new RopWriter();

        // RopPending: skipped.
        writer.UInt8(RopNotify.RopPending); writer.UInt16(0);

        // NewMail, message-level (M flag): FolderId, MessageId, MessageFlags, UnicodeFlag, MessageClass.
        writer.UInt8(RopNotify.RopId); writer.UInt32(7); writer.UInt8(0);
        writer.UInt16((ushort)(0x0002 | 0x8000));
        writer.UInt64(0x0C01000000000001); writer.UInt64(0xAA00000000000001);
        writer.UInt32(0x0001); writer.UInt8(1); writer.UnicodeZ("IPM.Note");

        // ObjectModified on a message: FolderId, MessageId, TagCount + tags.
        writer.UInt8(RopNotify.RopId); writer.UInt32(7); writer.UInt8(0);
        writer.UInt16((ushort)(0x0010 | 0x8000));
        writer.UInt64(0x0C01000000000001); writer.UInt64(0xAB00000000000001);
        writer.UInt16(1); writer.UInt32(PropertyTags.MessageFlags);

        // ObjectCreated on a FOLDER: FolderId, ParentFolderId, TagCount 0.
        writer.UInt8(RopNotify.RopId); writer.UInt32(7); writer.UInt8(0);
        writer.UInt16(0x0004);
        writer.UInt64(0xEE00000000000001); writer.UInt64(0x0801000000000001);
        writer.UInt16(0);

        var notifications = RopNotify.ParseNotifications(writer.ToArray());

        notifications.Should().HaveCount(3);
        notifications[0].Type.Should().Be(RopNotify.NotificationTypes.NewMail);
        notifications[0].IsMessage.Should().BeTrue();
        notifications[0].FolderId.Should().Be(0x0C01000000000001);
        notifications[0].MessageId.Should().Be(0xAA00000000000001);
        notifications[1].Type.Should().Be(RopNotify.NotificationTypes.ObjectModified);
        notifications[1].MessageId.Should().Be(0xAB00000000000001);
        notifications[2].Type.Should().Be(RopNotify.NotificationTypes.ObjectCreated);
        notifications[2].IsMessage.Should().BeFalse();
        notifications[2].FolderId.Should().Be(0xEE00000000000001);
        notifications[2].ParentFolderId.Should().Be(0x0801000000000001);
    }
}

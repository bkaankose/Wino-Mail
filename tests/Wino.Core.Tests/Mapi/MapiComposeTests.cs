using FluentAssertions;
using MimeKit;
using Wino.Core.Synchronizers.Mapi;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>The native draft and send path: the write-side encodings and the MIME lift.</summary>
public class MapiComposeTests
{
    [Fact]
    public void ModifyRecipients_EncodesSmtpRecipientRows()
    {
        var rop = RopMessageWrite.BuildModifyRecipients(1, [
            new RopMessageWrite.Recipient(RopMessageWrite.RecipientType.To, "Matt", "matt@example.com"),
            new RopMessageWrite.Recipient(RopMessageWrite.RecipientType.Cc, "cc@example.com", "cc@example.com"),
        ]);

        var reader = new RopReader(rop);
        reader.UInt8().Should().Be(RopMessageWrite.RopModifyRecipients);
        reader.UInt8(); reader.UInt8().Should().Be(1, "message handle");
        var columnCount = reader.UInt16();
        columnCount.Should().Be((ushort)RopMessageWrite.RecipientColumnsUsed.Length);
        for (var i = 0; i < columnCount; i++) reader.UInt32().Should().Be(RopMessageWrite.RecipientColumnsUsed[i]);
        reader.UInt16().Should().Be(2, "RowCount");

        // Row 0
        reader.UInt32().Should().Be(0, "RowId");
        reader.UInt8().Should().Be((byte)RopMessageWrite.RecipientType.To);
        var rowSize = reader.UInt16();
        var rowStart = reader.Position;
        reader.UInt16().Should().Be(0x021B, "SMTP type 3 | E | D | U");
        reader.UnicodeZ().Should().Be("matt@example.com", "EmailAddress");
        reader.UnicodeZ().Should().Be("Matt", "DisplayName");
        reader.UInt16().Should().Be(columnCount, "RecipientColumnCount");
        reader.UInt8().Should().Be(0x00, "standard PropertyRow");
        reader.UnicodeZ().Should().Be("Matt");
        reader.UnicodeZ().Should().Be("SMTP");
        reader.UnicodeZ().Should().Be("matt@example.com");
        reader.UnicodeZ().Should().Be("matt@example.com");
        (reader.Position - rowStart).Should().Be(rowSize, "RecipientRowSize covers exactly the row");

        // Row 1 header only
        reader.UInt32().Should().Be(1);
        reader.UInt8().Should().Be((byte)RopMessageWrite.RecipientType.Cc);
    }

    [Fact]
    public void FolderServerId_Is21BytesWithOursSet()
    {
        var id = RopMessageWrite.FolderServerId(0x0901000000000001);
        id.Should().HaveCount(21);
        id[0].Should().Be(1, "Ours");
        BitConverter.ToUInt64(id, 1).Should().Be(0x0901000000000001);
        id.Skip(9).Should().AllBeEquivalentTo((byte)0, "MessageId and Instance are zero for a folder");
    }

    /// <summary>The send properties: a PtypServerId encodes as a counted binary, which the first live send tripped on.</summary>
    [Fact]
    public void SetProperties_EncodesServerIdAndDeleteAfterSubmit()
    {
        var rop = RopMessageOps.BuildSetProperties(1, [
            TaggedPropertyValue.Binary(RopMessageWrite.SentMailSvrEID, RopMessageWrite.FolderServerId(0x0901000000000001)),
            TaggedPropertyValue.Boolean(RopMessageWrite.DeleteAfterSubmit, true),
        ]);

        var reader = new RopReader(rop);
        reader.Bytes(3); reader.UInt16();                       // header + PropertyValueSize
        reader.UInt16().Should().Be(2);
        reader.UInt32().Should().Be(RopMessageWrite.SentMailSvrEID);
        reader.UInt16().Should().Be(21, "counted ServerId");
        reader.UInt8().Should().Be(1, "Ours");
        reader.UInt64().Should().Be(0x0901000000000001);
        reader.Bytes(12);
        reader.UInt32().Should().Be(RopMessageWrite.DeleteAfterSubmit);
        reader.UInt8().Should().Be(1);
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void FolderRops_Encode()
    {
        var create = new RopReader(RopFolder.BuildCreateFolder(1, 2, "Projects"));
        create.UInt8().Should().Be(RopFolder.RopCreateFolder);
        create.UInt8(); create.UInt8().Should().Be(1); create.UInt8().Should().Be(2);
        create.UInt8().Should().Be(0x01, "generic folder");
        create.UInt8().Should().Be(1, "Unicode");
        create.UInt8().Should().Be(0, "OpenExisting");
        create.UInt8().Should().Be(0, "Reserved");
        create.UnicodeZ().Should().Be("Projects");
        create.UnicodeZ().Should().Be("", "comment");
        create.Remaining.Should().Be(0);

        var response = new RopWriter();
        response.UInt8(RopFolder.RopCreateFolder); response.UInt8(2); response.UInt32(0); response.UInt64(0xEE00000000000001); response.UInt8(0);
        RopFolder.ParseCreateFolder(new RopReader(response.ToArray())).Should().Be(0xEE00000000000001);

        var delete = new RopReader(RopFolder.BuildDeleteFolder(1, 0xEE00000000000001, RopFolder.DeleteFolderFlags.DeleteMessages | RopFolder.DeleteFolderFlags.DeleteFolders | RopFolder.DeleteFolderFlags.HardDelete));
        delete.Bytes(3); delete.UInt8().Should().Be(0x15); delete.UInt64().Should().Be(0xEE00000000000001); delete.Remaining.Should().Be(0);

        var move = new RopReader(RopFolder.BuildMoveFolder(1, 2, 0xEE00000000000001, "Projects"));
        move.Bytes(2); move.UInt8().Should().Be(1); move.UInt8().Should().Be(2);
        move.UInt8().Should().Be(0, "WantAsynchronous"); move.UInt8().Should().Be(1, "UseUnicode");
        move.UInt64().Should().Be(0xEE00000000000001); move.UnicodeZ().Should().Be("Projects"); move.Remaining.Should().Be(0);
    }

    [Fact]
    public void CreateMessage_ResponseCarriesOptionalMessageId()
    {
        var with = new RopWriter();
        with.UInt8(RopMessageWrite.RopCreateMessage); with.UInt8(1); with.UInt32(0); with.UInt8(1); with.UInt64(0xAB00000000000001);
        RopMessageWrite.ParseCreateMessage(new RopReader(with.ToArray())).Should().Be(0xAB00000000000001);

        var without = new RopWriter();
        without.UInt8(RopMessageWrite.RopCreateMessage); without.UInt8(1); without.UInt32(0); without.UInt8(0);
        RopMessageWrite.ParseCreateMessage(new RopReader(without.ToArray())).Should().BeNull();
    }

    [Fact]
    public void Mapper_LiftsRecipientsBodiesThreadingAndAttachments()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Testing", "testing@example.com"));
        mime.To.Add(new MailboxAddress("Matt", "matt@example.com"));
        mime.Cc.Add(new MailboxAddress(string.Empty, "cc@example.com"));
        mime.Subject = "Re: Orders";
        mime.MessageId = "new@example.com";
        mime.InReplyTo = "orig@example.com";
        mime.References.Add("root@example.com");
        mime.Importance = MessageImportance.High;

        var builder = new BodyBuilder { TextBody = "plain", HtmlBody = "<p>hi <img src=\"cid:logo\"></p>" };
        var logo = builder.LinkedResources.Add("logo.png", new byte[] { 1, 2, 3 }, ContentType.Parse("image/png"));
        logo.ContentId = "logo";
        builder.Attachments.Add("report.pdf", new byte[] { 4, 5, 6, 7 }, ContentType.Parse("application/pdf"));
        mime.Body = builder.ToMessageBody();

        var outgoing = MapiOutgoingMessageMapper.FromMime(mime);

        outgoing.Subject.Should().Be("Re: Orders");
        outgoing.TextBody.Should().Be("plain");
        outgoing.HtmlBody.Should().Contain("cid:logo");
        outgoing.Importance.Should().Be(2);
        outgoing.InternetMessageId.Should().Be("<new@example.com>");
        outgoing.InReplyTo.Should().Be("<orig@example.com>");
        outgoing.References.Should().Be("<root@example.com>");
        outgoing.Recipients.Select(r => (r.Type, r.SmtpAddress, r.DisplayName)).Should().Equal(
            (RopMessageWrite.RecipientType.To, "matt@example.com", "Matt"),
            (RopMessageWrite.RecipientType.Cc, "cc@example.com", "cc@example.com"));

        outgoing.Attachments.Should().HaveCount(2);
        var inline = outgoing.Attachments.Single(a => a.IsInline);
        inline.ContentId.Should().Be("logo");
        inline.Data.Should().Equal(1, 2, 3);
        var file = outgoing.Attachments.Single(a => !a.IsInline);
        file.FileName.Should().Be("report.pdf");
        file.MimeType.Should().Be("application/pdf");
        file.Data.Should().Equal(4, 5, 6, 7);
    }
}

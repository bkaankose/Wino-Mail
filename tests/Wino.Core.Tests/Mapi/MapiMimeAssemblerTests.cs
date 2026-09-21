using FluentAssertions;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Synchronizers.Mapi;
using Wino.Mapi;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// The MIME the app reads is assembled from MAPI properties (there is no MIME on the wire). These pin
/// the shape the reading pane and the attachment list depend on: original headers preserved minus the
/// MIME-structure ones, html+text alternative, inline images as linked resources with their cid, and
/// real attachments as attachment parts.
/// </summary>
public class MapiMimeAssemblerTests
{
    private static MailCopy Mail() => new()
    {
        Id = MapiExchangeSynchronizer.ToMailCopyId(0x1234000000000001),
        FileId = Guid.NewGuid(),
        Subject = "Fallback subject",
        FromName = "Fallback Sender",
        FromAddress = "fallback@example.com",
        CreationDate = new DateTime(2026, 9, 3, 15, 0, 0, DateTimeKind.Utc),
        MessageId = "<fallback@example.com>",
    };

    [Fact]
    public void Build_KeepsOriginalHeadersAndDropsMimeStructureOnes()
    {
        var content = new MapiMessageContent
        {
            TransportHeaders = "Received: from a by b\r\nFrom: Troy Compton <troy@example.com>\r\nTo: matt@example.com\r\nSubject: Re: Orders\r\nDate: Wed, 03 Sep 2026 14:30:00 +0000\r\nMessage-ID: <abc@example.com>\r\nContent-Type: multipart/mixed; boundary=\"old\"\r\nMIME-Version: 1.0\r\nX-Custom: kept\r\n",
            Html = "<p>Hello</p>",
            PlainText = "Hello",
        };

        var message = MapiMimeAssembler.Build(Mail(), content);

        message.From.Mailboxes.Single().Address.Should().Be("troy@example.com", "the original header wins over the row");
        message.Subject.Should().Be("Re: Orders");
        message.MessageId.Should().Be("abc@example.com");
        message.Headers["X-Custom"].Should().Be("kept");
        message.Headers["Received"].Should().Be("from a by b");
        message.Headers.Contains(HeaderId.ContentType).Should().BeFalse("the original body's Content-Type must not leak onto the message");
        message.Body.ContentType.MimeType.Should().Be("multipart/alternative", "the assembled body, not the original one");
        message.HtmlBody.Should().Be("<p>Hello</p>");
        message.TextBody.Should().Be("Hello");
    }

    [Fact]
    public void Build_WithoutHeaders_FallsBackToTheRowAndDisplayTo()
    {
        var message = MapiMimeAssembler.Build(Mail(), new MapiMessageContent { PlainText = "text only" }, displayTo: "Matt; someone@example.com");

        message.From.Mailboxes.Single().Address.Should().Be("fallback@example.com");
        message.To.Count.Should().Be(2);
        message.Subject.Should().Be("Fallback subject");
        message.MessageId.Should().Be("fallback@example.com");
        message.Date.UtcDateTime.Should().Be(new DateTime(2026, 9, 3, 15, 0, 0, DateTimeKind.Utc));
        message.TextBody.Should().Be("text only");
        message.HtmlBody.Should().BeNull();
    }

    [Fact]
    public void Build_InlineImageIsLinkedResource_FileIsAttachment()
    {
        var content = new MapiMessageContent { Html = "<img src=\"cid:img1@example\">" };
        content.Attachments.Add(new MapiAttachmentInfo(0, 1, 3, "logo.png", "image/png", "<img1@example>", IsHidden: true) { Data = [1, 2, 3] });
        content.Attachments.Add(new MapiAttachmentInfo(1, 1, 4, "report.pdf", "application/pdf", null, IsHidden: false) { Data = [4, 5, 6, 7] });
        content.Attachments.Add(new MapiAttachmentInfo(2, 5, 0, "embedded", null, null, IsHidden: false));   // embedded message, no data: skipped

        var message = MapiMimeAssembler.Build(Mail(), content);

        message.Attachments.Should().ContainSingle().Which.Should().BeOfType<MimePart>().Which.FileName.Should().Be("report.pdf");

        var linked = message.BodyParts.OfType<MimePart>().Single(p => p.ContentId == "img1@example");
        linked.ContentType.MimeType.Should().Be("image/png");
        linked.ContentDisposition!.Disposition.Should().Be(ContentDisposition.Inline);

        // Round-trips through a real serializer, which is what the file cache does.
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        var reloaded = MimeMessage.Load(stream);
        reloaded.Attachments.Should().HaveCount(1);
        reloaded.HtmlBody.Should().Contain("cid:img1@example");
    }

    [Fact]
    public void Build_MeetingMessage_LaysTheCalendarPartBesideTheBodies()
    {
        var content = new MapiMessageContent { Html = "<p>Meet</p>", PlainText = "Meet" };
        const string ics = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nEND:VCALENDAR\r\n";

        var message = MapiMimeAssembler.Build(Mail(), content, calendarPart: ics, calendarMethod: "REQUEST");

        var calendar = message.BodyParts.OfType<TextPart>().Single(p => p.ContentType.IsMimeType("text", "calendar"));
        calendar.ContentType.Parameters["method"].Should().Be("REQUEST");
        calendar.Text.Should().Contain("METHOD:REQUEST");
        message.HtmlBody.Should().Be("<p>Meet</p>");
    }

    [Fact]
    public void MailCopyId_RoundTrips()
    {
        var id = MapiExchangeSynchronizer.ToMailCopyId(0x0C01000000000001);
        id.Should().Be("mapi:0C01000000000001");
        MapiExchangeSynchronizer.TryParseMailCopyId(id, out var parsed).Should().BeTrue();
        parsed.Should().Be(0x0C01000000000001);
        MapiExchangeSynchronizer.TryParseMailCopyId("AAMkAD...", out _).Should().BeFalse("an EWS id is not addressable over MAPI");
    }
}

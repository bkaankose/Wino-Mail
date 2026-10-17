using System.IO;
using System.Text;
using FluentAssertions;
using MailKit;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Extensions;
using Wino.Services.Extensions;
using Xunit;

namespace Wino.Core.Tests.Models;

public class MimeHeaderCharsetRecoveryTests
{
    private const string CyrillicSubject = "Запрос сменить пароль на Компьютерный форум Ru.Board";
    private const string CyrillicSender = "Компьютерный форум";
    private const string CyrillicBody = "Компьютерный форум Ru.Board\r\nhttp://forum.ru-board.com/board.cgi\r\n";

    public MimeHeaderCharsetRecoveryTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void RecoverHeaderCharsets_DecodesRawWindows1251HeadersWithBodyCharset()
    {
        var windows1251 = Encoding.GetEncoding(1251);
        var original = LoadMessage(BuildSinglePartMessage(windows1251, "windows-1251", CyrillicSubject, CyrillicSender, CyrillicBody));

        // MimeKit alone falls back to ISO-8859-1 for these bytes.
        original.Subject.Should().NotBe(CyrillicSubject);

        var recovered = original.RecoverHeaderCharsets();

        recovered.Should().NotBeSameAs(original);
        recovered.Subject.Should().Be(CyrillicSubject);
        recovered.From.Mailboxes.Should().ContainSingle().Which.Name.Should().Be(CyrillicSender);
        recovered.From.Mailboxes.Should().ContainSingle().Which.Address.Should().Be("forum@ru-board.com");
        recovered.TextBody.Should().Be(CyrillicBody);
        recovered.Headers[recovered.Headers.IndexOf(HeaderId.Subject)].RawValue
            .Should().Equal(original.Headers[original.Headers.IndexOf(HeaderId.Subject)].RawValue, "raw header bytes stay untouched");
    }

    [Fact]
    public void RecoverHeaderCharsets_KeepsValidUtf8HeadersWhenBodyUsesAnotherCharset()
    {
        var windows1251 = Encoding.GetEncoding(1251);
        var raw = BuildRawMessage(
            Encoding.UTF8.GetBytes($"From: {CyrillicSender} <forum@ru-board.com>\r\nSubject: {CyrillicSubject}\r\n"),
            windows1251,
            "windows-1251",
            CyrillicBody);
        var original = LoadMessage(raw);

        var recovered = original.RecoverHeaderCharsets();

        recovered.Should().BeSameAs(original);
        recovered.Subject.Should().Be(CyrillicSubject);
        recovered.From.Mailboxes.Should().ContainSingle().Which.Name.Should().Be(CyrillicSender);
    }

    [Fact]
    public void RecoverHeaderCharsets_KeepsEncodedWordHeaders()
    {
        var windows1251 = Encoding.GetEncoding(1251);
        var encodedSubject = $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(CyrillicSubject))}?=";
        var raw = BuildRawMessage(
            Encoding.ASCII.GetBytes($"From: Ru-Board <forum@ru-board.com>\r\nSubject: {encodedSubject}\r\n"),
            windows1251,
            "windows-1251",
            CyrillicBody);
        var original = LoadMessage(raw);

        var recovered = original.RecoverHeaderCharsets();

        recovered.Should().BeSameAs(original);
        recovered.Subject.Should().Be(CyrillicSubject);
    }

    [Fact]
    public void RecoverHeaderCharsets_ReturnsSameInstanceWithoutUsableBodyCharset()
    {
        var windows1251 = Encoding.GetEncoding(1251);
        var original = LoadMessage(BuildSinglePartMessage(windows1251, null, CyrillicSubject, CyrillicSender, CyrillicBody));

        var recovered = original.RecoverHeaderCharsets();

        recovered.Should().BeSameAs(original);
    }

    [Fact]
    public void RecoverHeaderCharsets_UsesCharsetOfNestedTextPartForMultibyteEncodings()
    {
        var shiftJis = Encoding.GetEncoding("shift_jis");
        const string japaneseSubject = "パスワード変更のご案内";
        const string japaneseSender = "掲示板";
        const string japaneseBody = "こんにちは";
        const string japaneseFileName = "添付.txt";

        var headers = shiftJis.GetBytes($"From: {japaneseSender} <board@example.jp>\r\nSubject: {japaneseSubject}\r\n");
        var body = shiftJis.GetBytes(
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\r\n\r\n" +
            "--outer\r\n" +
            "Content-Type: multipart/alternative; boundary=\"inner\"\r\n\r\n" +
            "--inner\r\n" +
            "Content-Type: text/plain\r\n\r\n" +
            "plain\r\n" +
            "--inner\r\n" +
            "Content-Type: text/html; charset=shift_jis\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n\r\n" +
            $"<p>{japaneseBody}</p>\r\n" +
            "--inner--\r\n" +
            "--outer\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Disposition: attachment; filename=\"{japaneseFileName}\"\r\n\r\n" +
            "attachment\r\n" +
            "--outer--\r\n");
        var original = LoadMessage([.. headers, .. body]);

        var recovered = original.RecoverHeaderCharsets();

        recovered.Should().NotBeSameAs(original);
        recovered.Subject.Should().Be(japaneseSubject);
        recovered.From.Mailboxes.Should().ContainSingle().Which.Name.Should().Be(japaneseSender);
        recovered.HtmlBody.Should().Contain(japaneseBody);
        recovered.Attachments.Should().ContainSingle().Which.ContentDisposition.FileName.Should().Be(japaneseFileName);
    }

    [Fact]
    public void GetBodyFallbackEncoding_IgnoresCharsetsMimeKitAlreadyTries()
    {
        var utf8Message = LoadMessage(BuildSinglePartMessage(Encoding.UTF8, "utf-8", "Subject", "Sender", "body"));
        var latin1Message = LoadMessage(BuildSinglePartMessage(Encoding.Latin1, "iso-8859-1", "Subject", "Sender", "body"));

        MimeHeaderCharsetRecovery.GetBodyFallbackEncoding(utf8Message).Should().BeNull();
        MimeHeaderCharsetRecovery.GetBodyFallbackEncoding(latin1Message).Should().BeNull();
    }

    [Fact]
    public void GetMailDetails_DecodesRawImapHeadersWithBodyStructureCharset()
    {
        var windows1251 = Encoding.GetEncoding(1251);
        var summary = CreateEnvelopeOnlySummary(windows1251);
        summary.Headers = CreateRawHeaders(windows1251,
            $"Subject: {CyrillicSubject}",
            $"From: {CyrillicSender} <forum@ru-board.com>");

        var copy = summary.GetMailDetails(new MailItemFolder { Id = Guid.NewGuid(), UidValidity = 7 });

        copy.Subject.Should().Be(CyrillicSubject);
        copy.FromName.Should().Be(CyrillicSender);
        copy.FromAddress.Should().Be("forum@ru-board.com");
    }

    [Fact]
    public void GetMailDetails_FallsBackToEnvelopeWithoutRawHeaders()
    {
        var windows1251 = Encoding.GetEncoding(1251);
        var summary = CreateEnvelopeOnlySummary(windows1251);

        var copy = summary.GetMailDetails(new MailItemFolder { Id = Guid.NewGuid(), UidValidity = 7 });

        copy.Subject.Should().Be(summary.Envelope.Subject);
        copy.FromName.Should().Be("Mojibake");
    }

    [Fact]
    public void GetMailDetails_KeepsEnvelopeWhenRawHeadersAreValidUtf8()
    {
        var windows1251 = Encoding.GetEncoding(1251);
        var summary = CreateEnvelopeOnlySummary(windows1251);
        summary.Envelope.Subject = CyrillicSubject;
        summary.Headers = CreateRawHeaders(Encoding.UTF8, $"Subject: {CyrillicSubject}");

        var copy = summary.GetMailDetails(new MailItemFolder { Id = Guid.NewGuid(), UidValidity = 7 });

        copy.Subject.Should().Be(CyrillicSubject);
    }

    private static MessageSummary CreateEnvelopeOnlySummary(Encoding bodyEncoding)
    {
        var envelope = new Envelope
        {
            Subject = Encoding.Latin1.GetString(bodyEncoding.GetBytes(CyrillicSubject)),
            Date = DateTimeOffset.UtcNow,
        };
        envelope.From.Add(new MailboxAddress("Mojibake", "forum@ru-board.com"));

        return new MessageSummary(0)
        {
            UniqueId = new UniqueId(42),
            Flags = MessageFlags.None,
            InternalDate = DateTimeOffset.UtcNow,
            Envelope = envelope,
            Body = new BodyPartText(new ContentType("text", "plain") { Charset = bodyEncoding.WebName }, "1"),
        };
    }

    private static HeaderList CreateRawHeaders(Encoding encoding, params string[] lines)
    {
        var headers = new HeaderList();

        foreach (var line in lines)
        {
            Header.TryParse(ParserOptions.Default, encoding.GetBytes(line), out var header).Should().BeTrue();
            headers.Add(header);
        }

        return headers;
    }

    private static byte[] BuildSinglePartMessage(Encoding encoding, string charset, string subject, string senderName, string body)
        => BuildRawMessage(encoding.GetBytes($"From: {senderName} <forum@ru-board.com>\r\nSubject: {subject}\r\n"), encoding, charset, body);

    private static byte[] BuildRawMessage(byte[] rawHeaders, Encoding bodyEncoding, string charset, string body)
    {
        var contentType = charset == null ? "text/plain" : $"text/plain; charset={charset}";
        var rest = bodyEncoding.GetBytes(
            "To: user@example.com\r\n" +
            "Date: Tue, 13 Oct 2026 08:23:02 +0300\r\n" +
            "MIME-Version: 1.0\r\n" +
            $"Content-Type: {contentType}\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n\r\n" +
            body);

        return [.. rawHeaders, .. rest];
    }

    private static MimeMessage LoadMessage(byte[] raw)
        => MimeMessage.Load(new MemoryStream(raw));
}

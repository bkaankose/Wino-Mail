using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using MailKit;
using MimeKit;

namespace MailCal.Core.Domain.Extensions;

/// <summary>
/// Recovers header text that a sender wrote as raw 8-bit bytes instead of RFC 2047 encoded words.
/// </summary>
/// <remarks>
/// MimeKit decodes unencoded 8-bit header text by trying UTF-8, then <see cref="ParserOptions.CharsetEncoding"/>,
/// then ISO-8859-1, and keeps the first decoding without invalid bytes. The default options leave the middle
/// candidate at UTF-8, so a Windows-1251 or Shift_JIS subject ends up decoded as ISO-8859-1 mojibake.
/// Senders that skip header encoding almost always use the body charset for their headers too, so the body
/// charset is supplied as the middle candidate. Valid UTF-8 headers keep winning, and every charset MimeKit
/// can resolve is supported.
/// </remarks>
public static class MimeHeaderCharsetRecovery
{
    private const int Utf8CodePage = 65001;
    private const int Latin1CodePage = 28591;
    private const int AsciiCodePage = 20127;

    /// <summary>
    /// Returns the message with its headers decoded through the body charset when any header
    /// carries 8-bit bytes that are not valid UTF-8. Returns the same instance when nothing needs recovery.
    /// Header bytes are preserved unless an address display name needs the text decoder (see
    /// <see cref="TryGetRecoveredAddresses"/>); those address headers are rewritten as encoded words.
    /// </summary>
    public static MimeMessage RecoverHeaderCharsets(this MimeMessage message)
    {
        if (message == null)
            return null;

        var fallbackEncoding = GetBodyFallbackEncoding(message);

        if (fallbackEncoding == null || !NeedsRecovery(message))
            return message;

        try
        {
            using var stream = new MemoryStream();

            message.WriteTo(stream);
            stream.Position = 0;

            var recovered = MimeMessage.Load(CreateParserOptions(fallbackEncoding), stream);

            RecoverAddressNames(recovered, fallbackEncoding);

            return recovered;
        }
        catch
        {
            return message;
        }
    }

    /// <summary>
    /// Decodes a header value, using the fallback charset only when the raw bytes are 8-bit and not valid UTF-8.
    /// </summary>
    public static string GetRecoveredValue(Header header, Encoding fallbackEncoding)
    {
        if (header == null)
            return null;

        return fallbackEncoding != null && NeedsRecovery(header)
            ? header.GetValue(fallbackEncoding)
            : header.Value;
    }

    /// <summary>
    /// Parses an address header, using the fallback charset for display names only when the raw bytes
    /// are 8-bit and not valid UTF-8. Returns false when the header does not need recovery or does not parse.
    /// </summary>
    /// <remarks>
    /// The address parser splits a raw display name wherever a multibyte trail byte is an ASCII atom character,
    /// which breaks Shift_JIS, GBK and Big5 names. The text decoder tokenizes on whitespace only, so the header
    /// is decoded as text first and the resulting Unicode string is parsed as an address list.
    /// </remarks>
    public static bool TryGetRecoveredAddresses(Header header, Encoding fallbackEncoding, out InternetAddressList addresses)
    {
        addresses = null;

        if (header == null || fallbackEncoding == null || !NeedsRecovery(header))
            return false;

        return InternetAddressList.TryParse(header.GetValue(fallbackEncoding), out addresses);
    }

    /// <summary>
    /// Returns true when any header of the message or its parts carries 8-bit bytes that are not valid UTF-8.
    /// </summary>
    public static bool NeedsRecovery(MimeMessage message)
        => message != null && EnumerateHeaders(message).Any(NeedsRecovery);

    /// <summary>
    /// Returns true when the raw header value carries 8-bit bytes that are not valid UTF-8.
    /// MimeKit decodes such a value as ISO-8859-1 unless a better charset is supplied.
    /// </summary>
    public static bool NeedsRecovery(Header header)
    {
        var raw = header?.RawValue;

        if (raw == null || raw.Length == 0)
            return false;

        var hasHighBytes = false;

        foreach (var value in raw)
        {
            if (value >= 0x80)
            {
                hasHighBytes = true;
                break;
            }
        }

        return hasHighBytes && !Utf8.IsValid(raw);
    }

    /// <summary>
    /// Picks the charset of the first text part that declares one MimeKit can resolve and that MimeKit
    /// does not already try on its own. Returns null when no such charset exists.
    /// </summary>
    public static Encoding GetBodyFallbackEncoding(MimeMessage message)
    {
        if (message == null)
            return null;

        foreach (var entity in EnumerateEntities(message.Body))
        {
            if (entity is not TextPart textPart)
                continue;

            var encoding = GetUsableEncoding(textPart.ContentType);

            if (encoding != null)
                return encoding;
        }

        return null;
    }

    /// <summary>
    /// Picks the body fallback charset from an IMAP BODYSTRUCTURE, so messages that are only
    /// known by their envelope can still have their raw headers decoded.
    /// </summary>
    public static Encoding GetBodyFallbackEncoding(BodyPart bodyStructure)
    {
        foreach (var part in EnumerateBodyParts(bodyStructure))
        {
            if (part is not BodyPartText textPart)
                continue;

            var encoding = GetUsableEncoding(textPart.ContentType);

            if (encoding != null)
                return encoding;
        }

        return null;
    }

    private static void RecoverAddressNames(MimeMessage message, Encoding fallbackEncoding)
    {
        foreach (var header in message.Headers.ToList())
        {
            if (!TryGetRecoveredAddresses(header, fallbackEncoding, out var recovered))
                continue;

            switch (header.Id)
            {
                case HeaderId.Sender:
                    message.Sender = PickMailbox(message.Sender, recovered);
                    break;
                case HeaderId.ResentSender:
                    message.ResentSender = PickMailbox(message.ResentSender, recovered);
                    break;
                default:
                    ReplaceAddresses(GetAddressList(message, header.Id), recovered);
                    break;
            }
        }
    }

    private static InternetAddressList GetAddressList(MimeMessage message, HeaderId headerId)
        => headerId switch
        {
            HeaderId.From => message.From,
            HeaderId.ReplyTo => message.ReplyTo,
            HeaderId.To => message.To,
            HeaderId.Cc => message.Cc,
            HeaderId.Bcc => message.Bcc,
            HeaderId.ResentFrom => message.ResentFrom,
            HeaderId.ResentReplyTo => message.ResentReplyTo,
            HeaderId.ResentTo => message.ResentTo,
            HeaderId.ResentCc => message.ResentCc,
            HeaderId.ResentBcc => message.ResentBcc,
            _ => null,
        };

    private static void ReplaceAddresses(InternetAddressList current, InternetAddressList recovered)
    {
        if (current == null || AddressesMatch(current, recovered))
            return;

        current.Clear();
        current.AddRange(recovered);
    }

    private static MailboxAddress PickMailbox(MailboxAddress current, InternetAddressList recovered)
    {
        var mailbox = recovered.Mailboxes.FirstOrDefault();

        if (mailbox == null || current == null || MailboxesMatch(current, mailbox))
            return current;

        return mailbox;
    }

    private static bool AddressesMatch(InternetAddressList current, InternetAddressList recovered)
    {
        var currentMailboxes = current.Mailboxes.ToList();
        var recoveredMailboxes = recovered.Mailboxes.ToList();

        return currentMailboxes.Count == recoveredMailboxes.Count
               && currentMailboxes.Zip(recoveredMailboxes).All(pair => MailboxesMatch(pair.First, pair.Second));
    }

    private static bool MailboxesMatch(MailboxAddress current, MailboxAddress recovered)
        => string.Equals(current.Name, recovered.Name, System.StringComparison.Ordinal)
           && string.Equals(current.Address, recovered.Address, System.StringComparison.OrdinalIgnoreCase);

    private static Encoding GetUsableEncoding(ContentType contentType)
    {
        Encoding encoding;

        try
        {
            encoding = contentType?.CharsetEncoding;
        }
        catch
        {
            return null;
        }

        if (encoding == null)
            return null;

        return encoding.CodePage is Utf8CodePage or Latin1CodePage or AsciiCodePage ? null : encoding;
    }

    private static ParserOptions CreateParserOptions(Encoding fallbackEncoding)
    {
        var options = ParserOptions.Default.Clone();
        options.CharsetEncoding = fallbackEncoding;

        return options;
    }

    private static IEnumerable<Header> EnumerateHeaders(MimeMessage message)
    {
        foreach (var header in message.Headers)
            yield return header;

        foreach (var entity in EnumerateEntities(message.Body))
        {
            foreach (var header in entity.Headers)
                yield return header;

            if (entity is MessagePart { Message: { } nested })
            {
                foreach (var header in EnumerateHeaders(nested))
                    yield return header;
            }
        }
    }

    private static IEnumerable<MimeEntity> EnumerateEntities(MimeEntity entity)
    {
        if (entity == null)
            yield break;

        yield return entity;

        if (entity is Multipart multipart)
        {
            foreach (var child in multipart)
            {
                foreach (var nested in EnumerateEntities(child))
                    yield return nested;
            }
        }
        else if (entity is MessagePart { Message.Body: { } nestedBody })
        {
            foreach (var nested in EnumerateEntities(nestedBody))
                yield return nested;
        }
    }

    private static IEnumerable<BodyPart> EnumerateBodyParts(BodyPart bodyPart)
    {
        if (bodyPart == null)
            yield break;

        yield return bodyPart;

        if (bodyPart is BodyPartMultipart multipart)
        {
            foreach (var child in multipart.BodyParts)
            {
                foreach (var nested in EnumerateBodyParts(child))
                    yield return nested;
            }
        }
        else if (bodyPart is BodyPartMessage { Body: { } nestedBody })
        {
            foreach (var nested in EnumerateBodyParts(nestedBody))
                yield return nested;
        }
    }
}

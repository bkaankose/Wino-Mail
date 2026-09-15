#nullable enable annotations
using System;
using System.IO;
using System.Linq;
using MimeKit;
using Wino.Mapi;
using Wino.Mapi.Rops;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// The compose window produces a MimeMessage; the server wants properties. This is the inverse of
/// <see cref="MapiMimeAssembler"/>: bodies, recipients, threading headers and attachments lifted out
/// of the MIME. The From is not carried: on-premises Exchange sends as the mailbox that submits.
/// </summary>
public static class MapiOutgoingMessageMapper
{
    public static MapiOutgoingMessage FromMime(MimeMessage mime)
    {
        var message = new MapiOutgoingMessage
        {
            Subject = mime.Subject ?? string.Empty,
            TextBody = mime.TextBody,
            HtmlBody = mime.HtmlBody,
            Importance = mime.Importance switch
            {
                MessageImportance.High => 2,
                MessageImportance.Low => 0,
                _ => 1,
            },
            InternetMessageId = string.IsNullOrEmpty(mime.MessageId) ? null : "<" + mime.MessageId.Trim('<', '>') + ">",
            InReplyTo = string.IsNullOrEmpty(mime.InReplyTo) ? null : "<" + mime.InReplyTo.Trim('<', '>') + ">",
            References = mime.References.Count > 0 ? string.Join(" ", mime.References.Select(r => "<" + r.Trim('<', '>') + ">")) : null,
            ReadReceiptRequested = mime.Headers.Contains("Disposition-Notification-To"),
        };

        AddRecipients(message, mime.To, RopMessageWrite.RecipientType.To);
        AddRecipients(message, mime.Cc, RopMessageWrite.RecipientType.Cc);
        AddRecipients(message, mime.Bcc, RopMessageWrite.RecipientType.Bcc);

        // Linked resources (cid: images) are inline and hidden; everything else is a file attachment.
        foreach (var part in mime.BodyParts.OfType<MimePart>())
        {
            var isLinked = !string.IsNullOrEmpty(part.ContentId) && (message.HtmlBody?.Contains("cid:" + part.ContentId, StringComparison.OrdinalIgnoreCase) ?? false);
            var isAttachment = part.IsAttachment;
            if (!isLinked && !isAttachment)
                continue;

            message.Attachments.Add(new MapiOutgoingAttachment(
                part.FileName ?? (isLinked ? part.ContentId : "attachment"),
                part.ContentType.MimeType,
                ReadContent(part),
                isLinked ? part.ContentId : null,
                isLinked));
        }

        return message;
    }

    private static void AddRecipients(MapiOutgoingMessage message, InternetAddressList addresses, RopMessageWrite.RecipientType type)
    {
        foreach (var mailbox in addresses.Mailboxes)
        {
            if (string.IsNullOrWhiteSpace(mailbox.Address))
                continue;

            message.Recipients.Add(new RopMessageWrite.Recipient(type, string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address : mailbox.Name, mailbox.Address));
        }
    }

    private static byte[] ReadContent(MimePart part)
    {
        using var stream = new MemoryStream();
        part.Content.DecodeTo(stream);
        return stream.ToArray();
    }
}

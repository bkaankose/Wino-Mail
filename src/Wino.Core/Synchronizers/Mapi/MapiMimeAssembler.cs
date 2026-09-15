#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MimeKit;
using MimeKit.Utils;
using Wino.Core.Domain.Entities.Mail;
using Wino.Mapi;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// Turns MAPI message content into the RFC 822 message the rest of the app reads. There is no MIME on
/// the MAPI wire: Exchange stores bodies as properties and attachments as sub-objects, so the message
/// is assembled here. The original transport headers are kept where the server has them, which is
/// what makes reply threading and "view source" honest.
/// </summary>
public static class MapiMimeAssembler
{
    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Transfer-Encoding", "Content-Disposition", "Content-ID", "MIME-Version",
    };

    /// <param name="mailItem">The local row, used for the fallback envelope when the server kept no transport headers.</param>
    /// <param name="content">Bodies, headers and attachments read off the wire.</param>
    /// <param name="displayTo">PidTagDisplayTo (semicolon-separated display names) for the To fallback; drafts and sent items have no headers.</param>
    /// <param name="calendarPart">An iCalendar text for meeting messages, laid beside the readable bodies.</param>
    /// <param name="calendarMethod">The iTIP method of <paramref name="calendarPart"/> (REQUEST, CANCEL, REPLY).</param>
    public static MimeMessage Build(MailCopy mailItem, MapiMessageContent content, string? displayTo = null, string? calendarPart = null, string? calendarMethod = null)
    {
        // A fresh MimeMessage mints its own Message-Id and Date; they are replaced below either by the
        // original headers or by the row's values, never kept.
        var message = new MimeMessage();
        message.Headers.Remove(HeaderId.MessageId);
        message.Headers.Remove(HeaderId.Date);

        if (!string.IsNullOrWhiteSpace(content.TransportHeaders))
        {
            // The stored headers describe the ORIGINAL body's encoding; those must not leak onto the
            // body assembled below, so the MIME structure headers are dropped and only the rest kept.
            var bytes = Encoding.UTF8.GetBytes(content.TransportHeaders.TrimEnd() + "\r\n\r\n");
            using var stream = new MemoryStream(bytes);
            var headers = HeaderList.Load(stream);
            foreach (var header in headers)
            {
                if (!DroppedHeaders.Contains(header.Field))
                    message.Headers.Add(header.Field, header.Value);
            }

            // MimeMessage caches Subject, Date and Message-Id; a header added to the raw list does not
            // reach the typed properties (address lists do). Push those three through their setters.
            if (headers[HeaderId.Subject] is { } subject)
                message.Subject = subject;
            if (headers[HeaderId.MessageId] is { } messageId)
                message.MessageId = messageId.Trim().Trim('<', '>');
            if (headers[HeaderId.Date] is { } dateText && DateUtils.TryParse(dateText, out var date))
                message.Date = date;
        }

        if (message.From.Count == 0 && !string.IsNullOrEmpty(mailItem.FromAddress))
            message.From.Add(new MailboxAddress(mailItem.FromName ?? string.Empty, mailItem.FromAddress));

        if (message.To.Count == 0 && !string.IsNullOrEmpty(displayTo))
        {
            foreach (var recipient in displayTo.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (MailboxAddress.TryParse(recipient, out var address))
                    message.To.Add(address);
                else
                    message.To.Add(new MailboxAddress(recipient, string.Empty));
            }
        }

        if (string.IsNullOrEmpty(message.Subject))
            message.Subject = mailItem.Subject ?? string.Empty;

        if (message.Headers[HeaderId.Date] is null)
            message.Date = new DateTimeOffset(DateTime.SpecifyKind(mailItem.CreationDate, DateTimeKind.Utc));

        if (string.IsNullOrEmpty(message.MessageId) && !string.IsNullOrEmpty(mailItem.MessageId))
            message.MessageId = mailItem.MessageId.Trim('<', '>');

        var builder = new BodyBuilder();
        if (content.PlainText is not null)
            builder.TextBody = content.PlainText;
        if (content.Html is not null)
            builder.HtmlBody = content.Html;
        if (content.PlainText is null && content.Html is null)
            builder.TextBody = string.Empty;

        foreach (var attachment in content.Attachments.Where(a => a.IsFile && a.Data is not null))
        {
            var name = attachment.FileName ?? $"attachment-{attachment.AttachNumber}";
            var contentType = ContentType.TryParse(attachment.MimeType ?? string.Empty, out var parsed) ? parsed : new ContentType("application", "octet-stream");

            var part = new MimePart(contentType)
            {
                Content = new MimeContent(new MemoryStream(attachment.Data!)),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = name,
            };

            // An inline image is referenced from the HTML by cid: and hidden from the attachment list.
            if (!string.IsNullOrEmpty(attachment.ContentId) && (attachment.IsHidden || content.Html?.Contains("cid:" + attachment.ContentId.Trim('<', '>'), StringComparison.OrdinalIgnoreCase) == true))
            {
                part.ContentId = attachment.ContentId.Trim('<', '>');
                part.ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = name };
                builder.LinkedResources.Add(part);
            }
            else
            {
                part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = name };
                builder.Attachments.Add(part);
            }
        }

        var body = builder.ToMessageBody();
        if (!string.IsNullOrEmpty(calendarPart))
        {
            // text/calendar sits beside the readable bodies as another alternative, the way Exchange's
            // own MIME conversion lays a meeting request out.
            var calendar = new TextPart("calendar") { Text = calendarPart };
            calendar.ContentType.Parameters["method"] = calendarMethod ?? "REQUEST";
            calendar.ContentType.Charset = "utf-8";
            calendar.ContentTransferEncoding = ContentEncoding.QuotedPrintable;

            if (body is MultipartAlternative alternative)
                alternative.Add(calendar);
            else if (body is Multipart mixed && mixed.Count > 0 && mixed[0] is MultipartAlternative nested)
                nested.Add(calendar);
            else
                body = new MultipartAlternative { body, calendar };
        }

        message.Body = body;
        return message;
    }
}

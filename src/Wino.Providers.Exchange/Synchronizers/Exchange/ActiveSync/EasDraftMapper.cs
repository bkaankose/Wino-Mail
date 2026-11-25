using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using MimeKit;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// Turns a composed draft into the application data of a 16.x Sync Add in the Drafts collection.
/// </summary>
internal static class EasDraftMapper
{
    /// <summary>
    /// The draft as MIME (AirSyncBase body type 4). Exchange parses it into the draft and keeps every header and
    /// part: recipients with Bcc, In-Reply-To, References, Message-ID, inline images and attachments. A Change
    /// with MIME adds to the existing recipients and attachments instead of replacing them, so a later save is an
    /// Add of the new version and a Delete of the old one.
    /// </summary>
    internal static XElement Mime(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return new XElement(EasDataMapper.Sync + "ApplicationData",
            new XElement(EasDataMapper.Body + "Body", new XElement(EasDataMapper.Body + "Type", "4"), Opaque(EasDataMapper.Body + "Data", stream.ToArray())));
    }

    /// <summary>
    /// The draft as the MS-ASEMAIL 16.0 draft fields, for a server that refuses MIME: recipients, subject,
    /// importance, the HTML (or text) body and every attachment and inline image. Threading headers have no field
    /// and are lost on the server copy; the local MIME keeps them for sending.
    /// </summary>
    internal static XElement Fields(MimeMessage message)
    {
        XNamespace email = EasDataMapper.Email;
        var body = EasDataMapper.Body;
        var data = new XElement(EasDataMapper.Sync + "ApplicationData");
        if (message.To.Count > 0)
            data.Add(new XElement(email + "To", message.To.ToString()));
        if (message.Cc.Count > 0)
            data.Add(new XElement(email + "Cc", message.Cc.ToString()));
        if (message.Bcc.Count > 0)
            data.Add(new XElement(EasDataMapper.Email2 + "Bcc", message.Bcc.ToString()));
        data.Add(new XElement(email + "Subject", EasWbxml.XmlSafe(message.Subject ?? string.Empty)));
        data.Add(new XElement(email + "Importance", message.Importance switch
        {
            MessageImportance.High => "2",
            MessageImportance.Low => "0",
            _ => "1"
        }));

        var html = message.HtmlBody;
        data.Add(html != null
            ? new XElement(body + "Body", new XElement(body + "Type", "2"), new XElement(body + "Data", EasWbxml.XmlSafe(html)))
            : new XElement(body + "Body", new XElement(body + "Type", "1"), new XElement(body + "Data", EasWbxml.XmlSafe(message.TextBody ?? string.Empty))));

        var attachments = new XElement(body + "Attachments");
        var index = 0;
        foreach (var entity in message.BodyParts)
        {
            if (IsBodyText(entity))
                continue;

            using var content = new MemoryStream();
            string contentType;
            string name;
            if (entity is MessagePart attachedMessage)
            {
                attachedMessage.Message?.WriteTo(content);
                contentType = "message/rfc822";
                name = entity.ContentDisposition?.FileName ?? (attachedMessage.Message?.Subject is { Length: > 0 } subject ? subject + ".eml" : "message.eml");
            }
            else if (entity is MimePart part)
            {
                part.Content?.DecodeTo(content);
                contentType = part.ContentType.MimeType;
                name = part.FileName ?? part.ContentId ?? "attachment";
            }
            else
                continue;

            var add = new XElement(body + "Add",
                new XElement(body + "ClientId", (index++).ToString(CultureInfo.InvariantCulture)),
                Opaque(body + "Content", content.ToArray()),
                new XElement(body + "DisplayName", EasWbxml.XmlSafe(name)),
                new XElement(body + "Method", "1"),
                new XElement(body + "ContentType", contentType));
            if (!string.IsNullOrEmpty(entity.ContentId))
                add.Add(new XElement(body + "ContentId", entity.ContentId));
            if (!entity.IsAttachment)
                add.Add(new XElement(body + "IsInline", "1"));
            attachments.Add(add);
        }
        if (attachments.HasElements)
            data.Add(attachments);
        return data;
    }

    private static bool IsBodyText(MimeEntity entity)
        => entity is TextPart text && !text.IsAttachment && string.IsNullOrEmpty(text.FileName) && (text.IsHtml || text.IsPlain);

    // Exchange rejects attachment content sent as an inline string (status 102); it must be opaque data.
    private static XElement Opaque(XName name, byte[] bytes) => new(name, new XAttribute(EasWbxml.Opaque, "base64"), Convert.ToBase64String(bytes));
}

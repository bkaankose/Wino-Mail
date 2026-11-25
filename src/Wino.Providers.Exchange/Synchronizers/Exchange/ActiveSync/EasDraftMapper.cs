using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MimeKit;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>One attachment of a composed draft, as a 16.x Sync adds it to the server copy.</summary>
internal sealed class EasDraftAttachment
{
    private readonly byte[] _content;
    private readonly string _name;
    private readonly string _contentType;
    private readonly string? _contentId;
    private readonly bool _isInline;
    private readonly string _method;

    internal EasDraftAttachment(byte[] content, string name, string contentType, string? contentId, bool isInline, bool isMessage)
    {
        _content = content;
        _name = name;
        _contentType = contentType;
        _contentId = contentId;
        _isInline = isInline;
        _method = isMessage ? "5" : "1";
        var header = Encoding.UTF8.GetBytes(string.Join("\n", _method, name, contentType, contentId ?? string.Empty, isInline ? "1" : "0") + "\n");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(content);
        Fingerprint = Convert.ToHexString(hash.GetHashAndReset());
    }

    internal bool IsInline => _isInline;

    /// <summary>Identifies what was sent: content, name, type, content id, inline state and method.</summary>
    internal string Fingerprint { get; }

    internal XElement ToAdd(string clientId)
    {
        var body = EasDataMapper.Body;
        var add = new XElement(body + "Add",
            new XElement(body + "ClientId", clientId),
            EasDraftMapper.Opaque(body + "Content", _content),
            new XElement(body + "DisplayName", EasWbxml.XmlSafe(_name)),
            new XElement(body + "Method", _method),
            new XElement(body + "ContentType", _contentType));
        if (!string.IsNullOrEmpty(_contentId))
            add.Add(new XElement(body + "ContentId", _contentId));
        if (_isInline)
            add.Add(new XElement(body + "IsInline"));
        return add;
    }
}

/// <summary>
/// Turns a composed draft into the application data of a 16.x Sync command in the Drafts collection.
/// </summary>
internal static class EasDraftMapper
{
    /// <summary>
    /// The draft as MIME (AirSyncBase body type 4), for the Add that creates it. Exchange parses it into the draft and
    /// keeps every header and part: recipients with Bcc, In-Reply-To, References, Message-ID, inline images and
    /// attachments.
    /// </summary>
    internal static XElement Mime(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return new XElement(EasDataMapper.Sync + "ApplicationData",
            new XElement(EasDataMapper.Body + "Body", new XElement(EasDataMapper.Body + "Type", "4"), Opaque(EasDataMapper.Body + "Data", stream.ToArray())));
    }

    /// <summary>
    /// The draft as the MS-ASEMAIL 16.0 draft fields, for the Change that saves a later version under the same
    /// ServerId: recipients, subject, importance, read and flag state, the HTML (or text) body, and the attachment
    /// changes. Every field the draft holds is written, because a Change deletes the in-schema properties it leaves out.
    /// </summary>
    internal static XElement Change(MimeMessage message, bool isRead, bool isFlagged, IEnumerable<string> deletedFileReferences,
        IEnumerable<XElement> addedAttachments)
    {
        XNamespace email = EasDataMapper.Email;
        var body = EasDataMapper.Body;
        var data = new XElement(EasDataMapper.Sync + "ApplicationData");
        if (message.To.Count > 0)
            data.Add(new XElement(email + "To", EasWbxml.XmlSafe(message.To.ToString())));
        if (message.Cc.Count > 0)
            data.Add(new XElement(email + "Cc", EasWbxml.XmlSafe(message.Cc.ToString())));
        if (message.Bcc.Count > 0)
            data.Add(new XElement(EasDataMapper.Email2 + "Bcc", EasWbxml.XmlSafe(message.Bcc.ToString())));
        if (message.ReplyTo.Count > 0)
            data.Add(new XElement(email + "ReplyTo", EasWbxml.XmlSafe(message.ReplyTo.ToString())));
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
        data.Add(new XElement(email + "Read", isRead ? "1" : "0"));
        data.Add(EasDataMapper.Flag(isFlagged));

        var attachments = new XElement(body + "Attachments",
            deletedFileReferences.Select(reference => new XElement(body + "Delete", new XElement(body + "FileReference", reference))),
            addedAttachments);
        if (attachments.HasElements)
            data.Add(attachments);
        return data;
    }

    /// <summary>
    /// What the stored copy of the draft takes from a Change the server accepted: subject, importance, read and flag
    /// state, and the attachments with their inline state.
    /// </summary>
    internal static XElement ChangeEffect(XElement change, IEnumerable<EasDraftAttachment> attachments)
    {
        XNamespace email = EasDataMapper.Email;
        var body = EasDataMapper.Body;
        return new XElement(EasDataMapper.Sync + "ApplicationData",
            change.Elements().Where(element => element.Name == email + "Subject" || element.Name == email + "Importance" || element.Name == email + "Read" || element.Name == email + "Flag")
                .Select(element => new XElement(element)),
            new XElement(body + "Attachments", attachments.Select(attachment => new XElement(body + "Attachment", new XElement(body + "IsInline", attachment.IsInline ? "1" : "0")))));
    }

    /// <summary>Every part of the draft that is not its text body: attachments, inline images and attached messages.</summary>
    internal static List<EasDraftAttachment> Attachments(MimeMessage message)
    {
        var result = new List<EasDraftAttachment>();
        foreach (var entity in message.BodyParts)
        {
            if (IsBodyText(entity))
                continue;

            using var content = new MemoryStream();
            if (entity is MessagePart attachedMessage)
            {
                attachedMessage.Message?.WriteTo(content);
                var name = entity.ContentDisposition?.FileName ?? (attachedMessage.Message?.Subject is { Length: > 0 } subject ? subject + ".eml" : "message.eml");
                result.Add(new EasDraftAttachment(content.ToArray(), name, "message/rfc822", entity.ContentId, !entity.IsAttachment, isMessage: true));
            }
            else if (entity is MimePart part)
            {
                part.Content?.DecodeTo(content);
                result.Add(new EasDraftAttachment(content.ToArray(), part.FileName ?? part.ContentId ?? "attachment", part.ContentType.MimeType, entity.ContentId,
                    !entity.IsAttachment, isMessage: false));
            }
        }
        return result;
    }

    /// <summary>A ClientId for the attachment at <paramref name="index"/> of one Change.</summary>
    internal static string AttachmentClientId(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static bool IsBodyText(MimeEntity entity)
        => entity is TextPart text && !text.IsAttachment && string.IsNullOrEmpty(text.FileName) && (text.IsHtml || text.IsPlain);

    // Exchange rejects attachment content sent as an inline string (status 102); it must be opaque data.
    internal static XElement Opaque(XName name, byte[] bytes) => new(name, new XAttribute(EasWbxml.Opaque, "base64"), Convert.ToBase64String(bytes));
}

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

    // Exchange rejects attachment content sent as an inline string (status 102); it must be opaque data.
    private static XElement Opaque(XName name, byte[] bytes) => new(name, new XAttribute(EasWbxml.Opaque, "base64"), Convert.ToBase64String(bytes));
}

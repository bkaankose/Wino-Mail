#nullable enable
using System;

namespace MailCal.Core.Domain.Models.Calendar;

/// <summary>
/// One ATTACH property of a VEVENT (RFC 5545 section 3.8.1.1, RFC 8607 parameters).
/// Either <see cref="Uri"/> or <see cref="InlineData"/> is set.
/// </summary>
public sealed class CalDavEventAttachment
{
    /// <summary>Prefix of <see cref="RemoteAttachmentId"/> for attachments stored inside the ICS (VALUE=BINARY).</summary>
    public const string InlineIdPrefix = "inline:";

    /// <summary>The ATTACH value when it is a URI, as written by the server. Empty for inline data.</summary>
    public string Uri { get; init; } = string.Empty;

    /// <summary>FILENAME parameter (or X-APPLE-FILENAME / X-FILENAME), else the last URI segment.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>FMTTYPE parameter. Empty when the server did not send one.</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>SIZE parameter, or the decoded length of inline data; 0 when unknown.</summary>
    public long Size { get; init; }

    /// <summary>MANAGED-ID parameter (RFC 8607). Empty for attachments the server does not manage.</summary>
    public string ManagedId { get; init; } = string.Empty;

    /// <summary>Decoded bytes of a VALUE=BINARY;ENCODING=BASE64 attachment.</summary>
    public byte[]? InlineData { get; init; }

    public bool IsInline => InlineData != null;

    /// <summary>
    /// The value stored in <c>CalendarAttachment.RemoteAttachmentId</c>: the MANAGED-ID when present,
    /// else the URI, else <see cref="InlineIdPrefix"/> plus a hash of the inline bytes.
    /// </summary>
    public string RemoteAttachmentId { get; init; } = string.Empty;
}

/// <summary>What a server answered to a managed attachment add or remove (RFC 8607 section 3.3).</summary>
public sealed class CalDavManagedAttachmentResult
{
    /// <summary>The new ETag of the calendar resource. Empty when the server did not return one.</summary>
    public string ETag { get; init; } = string.Empty;

    /// <summary>The Cal-Managed-ID response header of an add. Empty when the server did not return one.</summary>
    public string ManagedId { get; init; } = string.Empty;

    /// <summary>The Location header: the URL of the new attachment. Empty when absent.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>The updated calendar resource when the server returned it in the body; otherwise empty.</summary>
    public string IcsContent { get; init; } = string.Empty;
}

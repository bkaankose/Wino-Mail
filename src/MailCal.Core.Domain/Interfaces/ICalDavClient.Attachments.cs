#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>Event attachments: ATTACH parsing, downloads and RFC 8607 managed attachments.</summary>
public partial interface ICalDavClient
{
    /// <summary>
    /// Reads the ATTACH properties of the VEVENT that <paramref name="remoteEventId"/> names in
    /// <paramref name="icsContent"/>: the overriding instance when one exists, else the series
    /// master. Makes no request; returns an empty list for unparsable content.
    /// </summary>
    IReadOnlyList<CalDavEventAttachment> ReadEventAttachments(string icsContent, string remoteEventId);

    /// <summary>
    /// Downloads an attachment with GET into <paramref name="destination"/> and returns the number
    /// of bytes written. With <paramref name="useAccountCredentials"/> false no Authorization
    /// header is sent. Throws <see cref="InvalidDataException"/> when the body exceeds
    /// <paramref name="maximumBytes"/>.
    /// </summary>
    Task<long> DownloadAttachmentAsync(
        CalDavConnectionSettings connectionSettings,
        Uri attachmentUri,
        bool useAccountCredentials,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken = default);

    /// <summary>POST <c>?action=attachment-add</c> on a calendar resource (RFC 8607 section 3.3.1).</summary>
    /// <param name="eTag">Sent as If-Match when not empty.</param>
    Task<CalDavManagedAttachmentResult> AddManagedAttachmentAsync(
        CalDavConnectionSettings connectionSettings,
        string exactHref,
        string eTag,
        string fileName,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken = default);

    /// <summary>POST <c>?action=attachment-remove&amp;managed-id=…</c> on a calendar resource (RFC 8607 section 3.3.3).</summary>
    /// <param name="eTag">Sent as If-Match when not empty.</param>
    Task<CalDavManagedAttachmentResult> RemoveManagedAttachmentAsync(
        CalDavConnectionSettings connectionSettings,
        string exactHref,
        string eTag,
        string managedId,
        CancellationToken cancellationToken = default);
}

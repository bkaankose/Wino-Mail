using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Calendar;

/// <summary>
/// Represents metadata for calendar event attachments.
/// Actual file content is downloaded on-demand.
/// </summary>
public class CalendarAttachment
{
    [PrimaryKey]
    public Guid Id { get; set; }

    /// <summary>
    /// The calendar item this attachment belongs to.
    /// </summary>
    public Guid CalendarItemId { get; set; }

    /// <summary>
    /// Remote identifier for the attachment from the provider (Outlook, Gmail, etc.).
    /// </summary>
    public string RemoteAttachmentId { get; set; }

    /// <summary>
    /// File name of the attachment.
    /// </summary>
    public string FileName { get; set; }

    /// <summary>
    /// Size of the attachment in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// MIME content type (e.g., "application/pdf", "image/png").
    /// </summary>
    public string ContentType { get; set; }

    /// <summary>
    /// Whether the attachment has been downloaded to local storage.
    /// </summary>
    public bool IsDownloaded { get; set; }

    /// <summary>
    /// Local file path where the attachment is stored (if downloaded).
    /// </summary>
    public string LocalFilePath { get; set; }

    /// <summary>
    /// When the attachment was last modified.
    /// </summary>
    public DateTimeOffset LastModified { get; set; }
}

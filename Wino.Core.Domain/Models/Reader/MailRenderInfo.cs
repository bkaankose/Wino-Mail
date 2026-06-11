using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// A mail address participating in a rendered or composed message.
/// </summary>
public record MailRecipientInfo(string Name, string Address);

/// <summary>
/// Attachment metadata of a rendered message. The content stays in the companion;
/// the UI requests extraction to a file on demand (IMailRenderService.ExtractAttachmentAsync)
/// using <paramref name="AttachmentIndex"/>, which is stable for the same message.
/// </summary>
public record MailAttachmentInfo(int AttachmentIndex, string FileName, long Size);

/// <summary>
/// Serializable result of rendering a mail in the companion process. Replaces direct
/// MimeMessage access in the UI: HTML is pre-rendered, inline resources are written to
/// the message's MIME resource directory, and attachments are extracted on demand.
/// </summary>
public class MailRenderInfo
{
    public string RenderHtml { get; set; }
    public string AccessibleText { get; set; }

    public string Subject { get; set; }
    public DateTime CreationDate { get; set; }
    public string FromName { get; set; }
    public string FromAddress { get; set; }

    public List<MailRecipientInfo> To { get; set; } = [];
    public List<MailRecipientInfo> Cc { get; set; } = [];
    public List<MailRecipientInfo> Bcc { get; set; } = [];

    public List<MailAttachmentInfo> Attachments { get; set; } = [];

    public UnsubscribeInfo UnsubscribeInfo { get; set; }

    /// <summary>S/MIME state; null when the message carries no S/MIME layers.</summary>
    public SmimeRenderInfo SmimeInfo { get; set; }

    /// <summary>Options the HTML was rendered with (image/style stripping).</summary>
    public MailRenderingOptions RenderingOptions { get; set; }

    /// <summary>Full path of the source .eml file (message source display, exports).</summary>
    public string EmlFilePath { get; set; }
}

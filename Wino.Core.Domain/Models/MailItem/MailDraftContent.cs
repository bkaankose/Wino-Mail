using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// An attachment of a draft under composition. <paramref name="FilePath"/> points to a
/// file both processes can read: either extracted from the existing draft MIME by the
/// companion or a file the user attached in the UI.
/// </summary>
public record DraftAttachmentInfo(string FileName, string FilePath, long Size);

/// <summary>
/// Serializable editing model of a draft. The UI composes against this DTO only; the
/// companion owns the MIME (IMailRenderService.GetDraftContentAsync /
/// SaveDraftContentAsync) and preserves identification headers (Message-Id, References,
/// Wino draft header) across saves.
/// </summary>
public class MailDraftContent
{
    public string Subject { get; set; }

    /// <summary>Editor HTML. On load this is the rendered draft body; on save the editor output.</summary>
    public string HtmlBody { get; set; }

    public List<MailRecipientInfo> To { get; set; } = [];
    public List<MailRecipientInfo> Cc { get; set; } = [];
    public List<MailRecipientInfo> Bcc { get; set; } = [];

    public MailImportance Importance { get; set; } = MailImportance.Normal;
    public bool IsReadReceiptRequested { get; set; }

    /// <summary>Sender identity, resolved from the selected alias in the UI.</summary>
    public string FromName { get; set; }
    public string FromAddress { get; set; }
    public string ReplyToAddress { get; set; }

    public List<DraftAttachmentInfo> Attachments { get; set; } = [];

    /// <summary>In-Reply-To header of the draft. Informational on load; ignored on save.</summary>
    public string InReplyTo { get; set; }
}

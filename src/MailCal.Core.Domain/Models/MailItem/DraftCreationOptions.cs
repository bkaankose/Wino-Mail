using MimeKit;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Launch;

namespace MailCal.Core.Domain.Models.MailItem;

public class DraftCreationOptions
{
    public DraftCreationReason Reason { get; set; }

    /// <summary>
    /// Optional plain-text content inserted at the editable start of a generated draft.
    /// </summary>
    public string InitialBodyText { get; set; }

    /// <summary>
    /// Used for forward/reply
    /// </summary>
    public ReferencedMessage ReferencedMessage { get; set; }

    /// <summary>
    /// Used to create mails from Mailto links
    /// </summary>
    public MailToUri MailToUri { get; set; }
}

public class ReferencedMessage
{
    public MailCopy MailCopy { get; set; }
    public MimeMessage MimeMessage { get; set; }
}

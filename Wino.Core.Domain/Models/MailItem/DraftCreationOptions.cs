using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Launch;

namespace Wino.Core.Domain.Models.MailItem;

public class DraftCreationOptions
{
    public DraftCreationReason Reason { get; set; }

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

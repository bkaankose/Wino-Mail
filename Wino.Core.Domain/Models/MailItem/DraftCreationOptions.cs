using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem;

public class DraftCreationOptions
{
    public DraftCreationReason Reason { get; set; }

    // Used for forward/reply
    public ReferencedMessage ReferencedMessage { get; set; }

    // Used to create mails from Mailto links
    public MailToParameters MailToParameters { get; set; }
}

public class MailToParameters
{
    public string To { get; set; }
    public string Cc { get; set; }
    public string Bcc { get; set; }
    public string Subject { get; set; }
    public string Body { get; set; }
}

public class ReferencedMessage
{
    public MailCopy MailCopy { get; set; }
    public MimeMessage MimeMessage { get; set; }
}

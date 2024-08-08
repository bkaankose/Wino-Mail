using System.Collections.Generic;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Launch;

namespace Wino.Core.Domain.Models.MailItem;

public class DraftCreationOptions
{
    public DraftCreationReason Reason { get; set; }

    // Used for forward/reply
    public ReferencedMessage ReferencedMessage { get; set; }

    // Used to create mails from Mailto links
    public MailToUri MailToUri { get; set; }
}

public class ReferencedMessage
{
    public MailCopy MailCopy { get; set; }
    public MimeMessage MimeMessage { get; set; }
}

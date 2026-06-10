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

/// <summary>
/// Reference to the mail being replied to / forwarded. This crosses the RPC pipe, so it
/// carries no MimeMessage: the companion loads the MIME from the shared storage using
/// <see cref="MailCopy.FileId"/>.
/// </summary>
public class ReferencedMessage
{
    public MailCopy MailCopy { get; set; }
}

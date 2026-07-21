using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Launch;

namespace Wino.Core.Domain.Models.MailItem;

public class DraftCreationOptions
{
    public DraftCreationReason Reason { get; set; }

    /// <summary>
    /// Used for forward/reply.
    /// </summary>
    public ReferencedMessage ReferencedMessage { get; set; }

    /// <summary>
    /// Used to create mails from Mailto links.
    /// </summary>
    public MailToUri MailToUri { get; set; }
}

public class ReferencedMessage
{
    /// <summary>
    /// Identifies the referenced mail in the shared database. The companion loads
    /// its metadata and MIME file locally instead of transferring either object.
    /// </summary>
    public Guid MailCopyUniqueId { get; set; }
}

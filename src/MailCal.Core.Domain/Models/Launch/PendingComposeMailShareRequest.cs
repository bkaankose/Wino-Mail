using System;

namespace MailCal.Core.Domain.Models.Launch;

public sealed class PendingComposeMailShareRequest
{
    public PendingComposeMailShareRequest(Guid draftUniqueId, MailShareRequest shareRequest)
    {
        DraftUniqueId = draftUniqueId;
        ShareRequest = shareRequest ?? throw new ArgumentNullException(nameof(shareRequest));
    }

    public Guid DraftUniqueId { get; }
    public MailShareRequest ShareRequest { get; }
}

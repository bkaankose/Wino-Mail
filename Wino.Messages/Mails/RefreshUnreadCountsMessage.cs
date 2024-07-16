using System;

namespace Wino.Messages.Mails
{
    public record RefreshUnreadCountsMessage(Guid AccountId);
}

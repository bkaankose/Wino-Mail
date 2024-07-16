using System;

namespace Wino.Messages.Client.Mails
{
    public record RefreshUnreadCountsMessage(Guid AccountId);
}

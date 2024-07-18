using System;

namespace Wino.Messaging.Client.Mails
{
    public record RefreshUnreadCountsMessage(Guid AccountId);
}

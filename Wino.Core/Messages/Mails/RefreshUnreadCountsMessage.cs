using System;

namespace Wino.Core.Messages.Mails
{
    public record RefreshUnreadCountsMessage(Guid AccountId);
}

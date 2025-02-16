using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.UI
{
    public record RefreshUnreadCountsMessage(Guid AccountId) : IUIMessage;
}

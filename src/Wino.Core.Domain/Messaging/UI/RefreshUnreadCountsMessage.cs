using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Messaging.UI;

public record RefreshUnreadCountsMessage(Guid AccountId) : IUIMessage;

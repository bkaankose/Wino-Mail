using System;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Messaging.UI;

public record RefreshUnreadCountsMessage(Guid AccountId) : IUIMessage;

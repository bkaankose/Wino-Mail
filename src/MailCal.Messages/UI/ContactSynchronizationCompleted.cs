using System;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Messaging.UI;

public record ContactSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result) : IClientMessage, IUIMessage;

using System;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Messaging.UI;

public record TaskSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result) : IClientMessage, IUIMessage;

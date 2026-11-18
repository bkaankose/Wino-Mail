using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Messaging.UI;

public record ContactSynchronizationCompleted(Guid AccountId, SynchronizationCompletedState Result) : IClientMessage, IUIMessage;

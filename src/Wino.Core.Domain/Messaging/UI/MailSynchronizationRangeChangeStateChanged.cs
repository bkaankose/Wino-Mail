using System;

namespace Wino.Core.Domain.Messaging.UI;

public record MailSynchronizationRangeChangeStateChanged(Guid AccountId, bool IsRunning)
    : UIMessageBase<MailSynchronizationRangeChangeStateChanged>;

using System;

namespace Wino.Messaging.UI;

/// <summary>Notifies mail presentation surfaces that one mailbox's local indicator choices changed.</summary>
public sealed record IntelligenceVisibilityChanged(Guid LocalAccountId)
    : UIMessageBase<IntelligenceVisibilityChanged>;

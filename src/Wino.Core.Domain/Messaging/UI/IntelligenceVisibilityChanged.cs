using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Messaging.UI;

/// <summary>Notifies mail presentation surfaces that one mailbox's local indicator choices changed.</summary>
public sealed record IntelligenceVisibilityChanged(
    Guid LocalAccountId,
    IReadOnlyList<string> ExcludedIndicatorIds)
    : UIMessageBase<IntelligenceVisibilityChanged>;

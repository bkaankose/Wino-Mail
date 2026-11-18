using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

/// <summary>
/// Emitted when calendar synchronization state for an account changes.
/// </summary>
public record AccountCalendarSynchronizationStateChanged(
    Guid AccountId,
    CalendarSynchronizationType SynchronizationType,
    bool IsSynchronizationInProgress,
    string SynchronizationStatus = "") : UIMessageBase<AccountCalendarSynchronizationStateChanged>;

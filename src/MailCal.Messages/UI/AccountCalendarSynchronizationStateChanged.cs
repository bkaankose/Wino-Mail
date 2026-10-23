using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

/// <summary>
/// Emitted when calendar synchronization state for an account changes.
/// </summary>
public record AccountCalendarSynchronizationStateChanged(
    Guid AccountId,
    CalendarSynchronizationType SynchronizationType,
    bool IsSynchronizationInProgress,
    string SynchronizationStatus = "") : UIMessageBase<AccountCalendarSynchronizationStateChanged>;

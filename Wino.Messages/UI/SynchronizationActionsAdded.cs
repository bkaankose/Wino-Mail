using System;
using System.Collections.Generic;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Messaging.UI;

/// <summary>
/// Sent when synchronization requests are queued for an account.
/// Contains grouped action descriptions for the UI to display.
/// </summary>
public record SynchronizationActionsAdded(
    Guid AccountId,
    string AccountName,
    List<SynchronizationActionItem> Actions) : UIMessageBase<SynchronizationActionsAdded>;
